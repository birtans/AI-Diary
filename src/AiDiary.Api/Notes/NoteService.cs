using System.Text;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Jobs;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Notes;

public sealed class NoteService(
    AppDbContext db,
    ObjectStorage storage,
    KeyHierarchyService crypto,
    JobQueue jobs)
{
    private const int MaxExcerptChars = 400;

    // ---------------------------------------------------------------- read

    public async Task<List<NoteListItem>> ListAsync(Guid userId, bool deleted, CancellationToken ct = default)
    {
        var notes = await db.Notes
            .AsNoTracking()
            .Where(n => n.UserId == userId && (deleted ? n.DeletedAt != null : n.DeletedAt == null))
            .OrderByDescending(n => n.EntryDate)
            .ThenByDescending(n => n.Id)
            .Take(500)
            .ToListAsync(ct);

        var ukek = crypto.UnwrapUkekUnderMaster(await RequireUserAsync(userId, ct));
        return notes.Select(n => new NoteListItem(
            n.Id,
            crypto.DecryptText(ukek, n.TitleEnc),
            n.EntryDate,
            n.CurrentVersion,
            n.DeletedAt,
            n.Origin)).ToList();
    }

    public async Task<NoteDetail> GetAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var note = await LoadNoteAsync(userId, noteId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);

        var current = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
        return new NoteDetail(
            note.Id,
            crypto.DecryptText(ukek, note.TitleEnc),
            note.EntryDate,
            note.CreatedAt,
            note.UpdatedAt,
            note.Timezone,
            note.LocationLat is { } lat && note.LocationLon is { } lon
                ? new Contracts.NoteLocationResponse(lat, lon, crypto.DecryptText(ukek, note.LocationLabelEnc))
                : null,
            note.CurrentVersion,
            note.Origin,
            note.DeletedAt,
            storage.PresignGet(current.S3Key),
            Convert.ToBase64String(crypto.UnwrapDek(ukek, note.WrappedDek)));
    }

    /// <summary>Fetches and decrypts the body of a version. Plaintext exists only in memory.</summary>
    public async Task<string> ReadBodyAsync(Guid userId, Note note, NoteVersion version, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var file = await storage.GetAsync(version.S3Key, ct);
        var plaintext = AiDiary.Crypto.FileEnvelope.Open(file, dek);
        var (_, body) = NoteContent.Split(Encoding.UTF8.GetString(plaintext));
        return body;
    }

    // --------------------------------------------------------------- create

    public async Task<NoteDetail> CreateAsync(Guid userId, CreateNoteRequest request, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var now = DateTimeOffset.UtcNow;

        var body = NoteContent.Normalize(request.Body);
        var title = string.IsNullOrWhiteSpace(request.Title) ? NoteContent.FirstLine(body) : request.Title.Trim();

        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            WrappedDek = crypto.WrapDek(ukek, crypto.CreateDek()),
            TitleEnc = crypto.EncryptText(ukek, title),
            EntryDate = request.EntryDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = now,
            UpdatedAt = now,
            Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone,
            LocationLat = request.Location?.Lat,
            LocationLon = request.Location?.Lon,
            LocationLabelEnc = request.Location?.Label is { Length: > 0 } label
                ? crypto.EncryptText(ukek, label)
                : null,
            CurrentVersion = 1,
            Origin = request.Origin == "agent" ? "agent" : "user",
        };

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        await WriteVersionAsync(userId, note, title, 1, body, dek, ct);

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(JobQueue.KindEmbedNote, new { note_id = note.Id }, ct: ct);

        return await GetAsync(userId, note.Id, ct);
    }

    // ----------------------------------------------------------------- save

    public async Task<NoteDetail> SaveAsync(Guid userId, Guid noteId, SaveNoteRequest request, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (note.DeletedAt is not null)
        {
            throw new ConflictException("The note is in the trash. Restore it before saving.");
        }
        if (request.ExpectedVersion != note.CurrentVersion)
        {
            throw new VersionConflictException(note.CurrentVersion);
        }

        var body = NoteContent.Normalize(request.Body);
        var hash = KeyHierarchyService.Sha256(Encoding.UTF8.GetBytes(body));
        var current = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
        if (hash.SequenceEqual(current.ContentHash))
        {
            // No-op save: identical plaintext. Never churn a version.
            return await GetAsync(userId, note.Id, ct);
        }

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var newVersion = note.CurrentVersion + 1;
        await WriteVersionAsync(userId, note, crypto.DecryptText(ukek, note.TitleEnc), newVersion, body, dek, ct);

        current.SupersededAt = DateTimeOffset.UtcNow;
        note.CurrentVersion = newVersion;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(JobQueue.KindEmbedNote, new { note_id = note.Id }, ct: ct);

        return await GetAsync(userId, note.Id, ct);
    }

    // -------------------------------------------------------------- history

    public async Task<List<NoteVersionItem>> ListVersionsAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(userId, noteId, ct);
        return note.Versions
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new NoteVersionItem(v.VersionNo, v.Filename, v.ByteSize, v.CreatedAt, v.SupersededAt))
            .ToList();
    }

    public async Task<DiffResponse> DiffAsync(Guid userId, Guid noteId, int versionA, int versionB, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(userId, noteId, ct);
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);

        var a = note.Versions.FirstOrDefault(v => v.VersionNo == versionA)
            ?? throw new NotFoundException($"Version {versionA} does not exist.");
        var b = note.Versions.FirstOrDefault(v => v.VersionNo == versionB)
            ?? throw new NotFoundException($"Version {versionB} does not exist.");

        var textA = await DecryptVersionAsync(dek, a.S3Key, ct);
        var textB = await DecryptVersionAsync(dek, b.S3Key, ct);

        var differ = new DiffPlex.Differ();
        var model = new DiffPlex.DiffBuilder.InlineDiffBuilder(differ)
            .BuildDiffModel(textA, textB);

        var html = new StringBuilder("<div class=\"diff\">");
        foreach (var line in model.Lines)
        {
            var css = line.Type switch
            {
                DiffPlex.DiffBuilder.Model.ChangeType.Inserted => "insert",
                DiffPlex.DiffBuilder.Model.ChangeType.Deleted => "delete",
                DiffPlex.DiffBuilder.Model.ChangeType.Modified => "modify",
                _ => "same",
            };
            html.Append("<div class=\"diff-line ")
                .Append(css)
                .Append("\"><span class=\"diff-sign\">")
                .Append(css switch { "insert" => "+", "delete" => "-", "modify" => "~", _ => " " })
                .Append("</span>")
                .Append(System.Net.WebUtility.HtmlEncode(line.Text ?? ""))
                .Append("</div>");
        }
        html.Append("</div>");
        return new DiffResponse(versionA, versionB, html.ToString());
    }

    public async Task<NoteDetail> RestoreVersionAsync(Guid userId, Guid noteId, int versionNo, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (note.DeletedAt is not null)
        {
            throw new ConflictException("The note is in the trash. Restore it before editing history.");
        }
        var source = note.Versions.FirstOrDefault(v => v.VersionNo == versionNo)
            ?? throw new NotFoundException($"Version {versionNo} does not exist.");

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var file = await storage.GetAsync(source.S3Key, ct);
        var (_, body) = NoteContent.Split(
            Encoding.UTF8.GetString(AiDiary.Crypto.FileEnvelope.Open(file, dek)));

        var newVersion = note.CurrentVersion + 1;
        await WriteVersionAsync(userId, note, crypto.DecryptText(ukek, note.TitleEnc), newVersion, body, dek, ct);

        note.Versions.First(v => v.VersionNo == note.CurrentVersion).SupersededAt = DateTimeOffset.UtcNow;
        note.CurrentVersion = newVersion;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(JobQueue.KindEmbedNote, new { note_id = note.Id }, ct: ct);

        return await GetAsync(userId, note.Id, ct);
    }

    // ----------------------------------------------------------------- meta

    public async Task<NoteDetail> PatchMetaAsync(Guid userId, Guid noteId, PatchNoteMetaRequest request, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (request.Title is { Length: > 0 } title)
        {
            note.TitleEnc = crypto.EncryptText(ukek, title.Trim());
        }
        if (request.EntryDate is { } entryDate)
        {
            note.EntryDate = entryDate;
        }
        if (!string.IsNullOrWhiteSpace(request.Timezone))
        {
            note.Timezone = request.Timezone;
        }
        if (request.ClearLocation)
        {
            note.LocationLat = null;
            note.LocationLon = null;
            note.LocationLabelEnc = null;
        }
        else if (request.Location is { } location)
        {
            note.LocationLat = location.Lat;
            note.LocationLon = location.Lon;
            note.LocationLabelEnc = location.Label is { Length: > 0 } label
                ? crypto.EncryptText(ukek, label)
                : null;
        }
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, note.Id, ct);
    }

    // -------------------------------------------------------- trash lifecycle

    public async Task SoftDeleteAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(userId, noteId, ct);
        if (note.DeletedAt is null)
        {
            note.DeletedAt = DateTimeOffset.UtcNow;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RestoreAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(userId, noteId, ct);
        if (note.DeletedAt is not null)
        {
            note.DeletedAt = null;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // -------------------------------------------------------------- helpers

    private async Task<string> DecryptVersionAsync(byte[] dek, string s3Key, CancellationToken ct)
    {
        var file = await storage.GetAsync(s3Key, ct);
        var plaintext = AiDiary.Crypto.FileEnvelope.Open(file, dek);
        var (_, body) = NoteContent.Split(Encoding.UTF8.GetString(plaintext));
        return body;
    }

    private async Task WriteVersionAsync(
        Guid userId,
        Note note,
        string title,
        int versionNo,
        string body,
        byte[] dek,
        CancellationToken ct)
    {
        var dto = new NoteDto
        {
            Id = note.Id,
            Title = title,
            EntryDate = note.EntryDate,
            CreatedAt = note.CreatedAt,
            Timezone = note.Timezone,
            Location = note.LocationLat is { } lat && note.LocationLon is { } lon
                ? new NoteLocation(lat, lon, null)
                : null,
            Origin = note.Origin,
        };
        var content = NoteContent.BuildFile(dto, versionNo, body);
        var plaintextBytes = Encoding.UTF8.GetBytes(content);
        var encrypted = AiDiary.Crypto.FileEnvelope.Seal(note.Id, versionNo, dek, plaintextBytes);
        var key = ObjectStorage.NoteObjectKey(userId, note.Id, versionNo);
        await storage.PutAsync(key, encrypted, ct);

        note.Versions.Add(new NoteVersion
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            VersionNo = versionNo,
            S3Key = key,
            Filename = NoteContent.ExportFilename(note.EntryDate, title),
            ByteSize = encrypted.Length,
            ContentHash = KeyHierarchyService.Sha256(Encoding.UTF8.GetBytes(body)),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task<Note> LoadNoteAsync(Guid userId, Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes
            .Include(n => n.Versions)
            .FirstOrDefaultAsync(n => n.Id == noteId && n.UserId == userId, ct)
            ?? throw new NotFoundException("Note not found.");
        return note;
    }

    private async Task<AppUser> RequireUserAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw new UnauthorizedAccessException("User does not exist.");
}

// --------------------------------------------------------------- exceptions

public class ApiException(string type, string title, int status) : Exception(title)
{
    public string Type { get; } = type;
    public int Status { get; } = status;
}

public sealed class NotFoundException(string message)
    : ApiException("urn:ai-diary:error:not-found", message, StatusCodes.Status404NotFound);

public sealed class ConflictException(string message)
    : ApiException("urn:ai-diary:error:conflict", message, StatusCodes.Status409Conflict);

public sealed class VersionConflictException(int currentVersion)
    : ApiException(
        "urn:ai-diary:error:version-conflict",
        "The note was modified since you loaded it.",
        StatusCodes.Status409Conflict)
{
    public int CurrentVersion { get; } = currentVersion;
}
