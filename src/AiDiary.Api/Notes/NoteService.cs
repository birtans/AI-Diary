using System.Text;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Jobs;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
            n.Origin,
            n.NeedsIndex)).ToList();
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
            Revision(current),
            note.Origin,
            note.DeletedAt,
            storage.PresignGet(current.S3Key),
            Convert.ToBase64String(crypto.UnwrapDek(ukek, note.WrappedDek)),
            note.NeedsIndex);
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
            HasText = BodyHasText(body),
            ContentChangedAt = ContentStamp(),
        };

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        await WriteVersionAsync(userId, note, title, 1, body, dek, ct);

        // Not indexed here: indexing happens on Save & Index and in the nightly run.
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return await GetAsync(userId, note.Id, ct);
    }

    // ----------------------------------------------------------------- save

    /// <summary>
    /// Writes the body into the working copy in place. The first edit on a
    /// later day than the working copy was last written freezes it as a
    /// retained version first, so each day's final state survives.
    /// </summary>
    public async Task<NoteDetail> SaveAsync(Guid userId, Guid noteId, SaveNoteRequest request, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        await using var tx = await LockNoteAsync(userId, noteId, ct);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (note.DeletedAt is not null)
        {
            throw new ConflictException("The note is in the trash. Restore it before saving.");
        }

        var body = NoteContent.Normalize(request.Body);
        var hash = KeyHierarchyService.Sha256(Encoding.UTF8.GetBytes(body));
        var current = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
        if (hash.SequenceEqual(current.ContentHash))
        {
            // No-op save: identical plaintext. Never rewrite the object. Checked
            // before ExpectedRevision so the loser of a debounced-autosave vs
            // blur-save race for the same edit sees success, not a conflict.
            return await GetAsync(userId, note.Id, ct);
        }
        if (request.ExpectedRevision != Revision(current))
        {
            throw new VersionConflictException(note.CurrentVersion, Revision(current));
        }

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var title = crypto.DecryptText(ukek, note.TitleEnc);
        if (LastWrittenOnEarlierDay(note, current) && !IsAlreadyRetained(note, current))
        {
            await FreezeAndAdvanceAsync(userId, note, current, title, body, dek, ct);
        }
        else
        {
            await OverwriteVersionAsync(note, current, title, body, dek, ct);
        }

        // Autosave never indexes; the note shows as not indexed until Save & Index.
        note.HasText = BodyHasText(body);
        note.ContentChangedAt = ContentStamp();
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await GetAsync(userId, note.Id, ct);
    }

    /// <summary>
    /// "Save &amp; Index": freezes the working copy as a retained version (its
    /// retention clock starts now), continues editing in a fresh working copy,
    /// and queues indexing. The freeze is skipped when the newest retained
    /// version already holds this text; indexing is skipped when the vectors
    /// are current.
    /// </summary>
    public async Task<NoteDetail> SaveVersionAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        if (!await SaveVersionAndIndexAsync(user, noteId, writtenAfter: null, ct))
        {
            throw new ConflictException("The note is in the trash. Restore it before saving a version.");
        }
        return await GetAsync(userId, noteId, ct);
    }

    /// <summary>
    /// Nightly run: versions and indexes one note. Only freezes a working copy
    /// written after <paramref name="writtenAfter"/>, so untouched notes don't
    /// gain a version every night.
    /// </summary>
    public async Task NightlyVersionAndIndexAsync(Guid userId, Guid noteId, DateTimeOffset writtenAfter, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        await SaveVersionAndIndexAsync(user, noteId, writtenAfter, ct);
    }

    /// <summary>Queues indexing for a note regardless of its state (seeding).</summary>
    public Task RequestIndexAsync(Guid noteId, CancellationToken ct = default) =>
        jobs.EnqueueUnlessPendingAsync(JobQueue.KindEmbedNote, new { note_id = noteId }, ct);

    /// <summary>False when the note is in the trash.</summary>
    private async Task<bool> SaveVersionAndIndexAsync(AppUser user, Guid noteId, DateTimeOffset? writtenAfter, CancellationToken ct)
    {
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        await using var tx = await LockNoteAsync(user.Id, noteId, ct);
        var note = await LoadNoteAsync(user.Id, noteId, ct);
        if (note.DeletedAt is not null)
        {
            return false;
        }

        var current = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
        var hasRetained = note.Versions.Any(v => v.SupersededAt != null);
        var shouldFreeze = !IsAlreadyRetained(note, current)
            && (writtenAfter is null || current.CreatedAt > writtenAfter)
            // An empty note that never had a version has nothing worth keeping.
            && (note.HasText || hasRetained);
        if (shouldFreeze)
        {
            var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
            var body = await DecryptVersionAsync(dek, current.S3Key, ct);
            await FreezeAndAdvanceAsync(user.Id, note, current, crypto.DecryptText(ukek, note.TitleEnc), body, dek, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        // Stale even without text: an emptied note's old vectors must go.
        if (note.IndexedContentAt != note.ContentChangedAt)
        {
            await RequestIndexAsync(note.Id, ct);
        }
        return true;
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
        await using var tx = await LockNoteAsync(userId, noteId, ct);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (note.DeletedAt is not null)
        {
            throw new ConflictException("The note is in the trash. Restore it before editing history.");
        }
        var source = note.Versions.FirstOrDefault(v => v.VersionNo == versionNo)
            ?? throw new NotFoundException($"Version {versionNo} does not exist.");

        var current = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
        if (source.ContentHash.SequenceEqual(current.ContentHash))
        {
            return await GetAsync(userId, note.Id, ct);
        }

        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var body = await DecryptVersionAsync(dek, source.S3Key, ct);
        var title = crypto.DecryptText(ukek, note.TitleEnc);

        // The text being replaced stays in history unless it is already there.
        if (IsAlreadyRetained(note, current))
        {
            await OverwriteVersionAsync(note, current, title, body, dek, ct);
        }
        else
        {
            await FreezeAndAdvanceAsync(userId, note, current, title, body, dek, ct);
        }
        note.HasText = BodyHasText(body);
        note.ContentChangedAt = ContentStamp();
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await RequestIndexAsync(note.Id, ct);

        return await GetAsync(userId, note.Id, ct);
    }

    // ----------------------------------------------------------------- meta

    public async Task<NoteDetail> PatchMetaAsync(Guid userId, Guid noteId, PatchNoteMetaRequest request, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var note = await LoadNoteAsync(userId, noteId, ct);

        // Title and date are indexed, so changing them makes the note stale,
        // but doesn't index it: that waits for Save & Index or the nightly run.
        if (request.Title is { Length: > 0 } title
            && title.Trim() != crypto.DecryptText(ukek, note.TitleEnc))
        {
            note.TitleEnc = crypto.EncryptText(ukek, title.Trim());
            note.ContentChangedAt = ContentStamp();
        }
        if (request.EntryDate is { } entryDate && entryDate != note.EntryDate)
        {
            note.EntryDate = entryDate;
            note.ContentChangedAt = ContentStamp();
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

    /// <summary>
    /// Moves a note to the trash and deletes its vectors. Restoring it doesn't
    /// re-index; it shows as not indexed until Save &amp; Index or the nightly run.
    /// </summary>
    public async Task SoftDeleteAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var tx = await LockNoteAsync(userId, noteId, ct);
        var note = await LoadNoteAsync(userId, noteId, ct);
        if (note.DeletedAt is null)
        {
            note.DeletedAt = DateTimeOffset.UtcNow;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            note.IndexedContentAt = null;
            await db.NoteEmbeddings.Where(e => e.NoteId == noteId).ExecuteDeleteAsync(ct);
            await jobs.CancelPendingAsync(JobQueue.KindEmbedNote, new { note_id = noteId }, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
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

    /// <summary>
    /// Permanently deletes a trashed note: every version object in storage,
    /// the version rows, the embedding, the note row, and chat citations of it.
    /// Storage first, as in the reaper, so a crash leaves a row rather than a
    /// dangling object.
    /// </summary>
    public async Task PurgeAsync(Guid userId, Guid noteId, CancellationToken ct = default)
    {
        await using var tx = await LockNoteAsync(userId, noteId, ct);
        var note = await LoadNoteAsync(userId, noteId, ct);

        if (note.DeletedAt is null)
        {
            throw new ConflictException("Only notes in the trash can be deleted permanently.");
        }

        await storage.DeleteManyAsync(note.Versions.Select(v => v.S3Key), ct);

        await db.NoteEmbeddings.Where(e => e.NoteId == noteId).ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE messages m SET "CitedNoteIds" = array_remove(m."CitedNoteIds", {noteId})
            FROM conversations c
            WHERE m."ConversationId" = c."Id" AND c."UserId" = {userId} AND {noteId} = ANY(m."CitedNoteIds")
            """, ct);
        db.NoteVersions.RemoveRange(note.Versions);
        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // -------------------------------------------------------------- helpers

    private async Task<string> DecryptVersionAsync(byte[] dek, string s3Key, CancellationToken ct)
    {
        var file = await storage.GetAsync(s3Key, ct);
        var plaintext = AiDiary.Crypto.FileEnvelope.Open(file, dek);
        var (_, body) = NoteContent.Split(Encoding.UTF8.GetString(plaintext));
        return body;
    }

    private static bool BodyHasText(string body) => !string.IsNullOrWhiteSpace(body);

    /// <summary>Now, at Postgres timestamptz precision, so it compares equal after a round trip.</summary>
    private static DateTimeOffset ContentStamp()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>Opaque optimistic-concurrency token: changes whenever the working copy's text does.</summary>
    private static string Revision(NoteVersion version) => Convert.ToBase64String(version.ContentHash);

    /// <summary>True when the newest retained version already holds the working copy's text.</summary>
    private static bool IsAlreadyRetained(Note note, NoteVersion current) =>
        note.Versions
            .Where(v => v.SupersededAt != null)
            .MaxBy(v => v.VersionNo) is { } latest
        && latest.ContentHash.SequenceEqual(current.ContentHash);

    /// <summary>Compares calendar days in the note's timezone (UTC when unknown).</summary>
    private static bool LastWrittenOnEarlierDay(Note note, NoteVersion current)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(note.Timezone, out var found) ? found : TimeZoneInfo.Utc;
        var lastWritten = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(current.CreatedAt, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        return lastWritten < today;
    }

    /// <summary>
    /// Freezes the working copy (starting its retention clock) and opens the
    /// next version number as the new working copy holding <paramref name="body"/>.
    /// </summary>
    private async Task FreezeAndAdvanceAsync(
        Guid userId,
        Note note,
        NoteVersion current,
        string title,
        string body,
        byte[] dek,
        CancellationToken ct)
    {
        var newVersion = note.CurrentVersion + 1;
        await WriteVersionAsync(userId, note, title, newVersion, body, dek, ct);
        current.SupersededAt = DateTimeOffset.UtcNow;
        note.CurrentVersion = newVersion;
    }

    /// <summary>
    /// Replaces the working copy's object and row in place. CreatedAt tracks
    /// when the row's content was last written: that is what history shows
    /// and what the once-per-day freeze compares against.
    /// </summary>
    private async Task OverwriteVersionAsync(
        Note note,
        NoteVersion version,
        string title,
        string body,
        byte[] dek,
        CancellationToken ct)
    {
        version.ByteSize = await PutVersionObjectAsync(note, title, version.VersionNo, version.S3Key, body, dek, ct);
        version.Filename = NoteContent.ExportFilename(note.EntryDate, title);
        version.ContentHash = KeyHierarchyService.Sha256(Encoding.UTF8.GetBytes(body));
        version.CreatedAt = DateTimeOffset.UtcNow;
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
        var key = ObjectStorage.NoteObjectKey(userId, note.Id, versionNo);
        var byteSize = await PutVersionObjectAsync(note, title, versionNo, key, body, dek, ct);

        // Added to the DbSet explicitly, not via note.Versions.Add(...): when
        // `note` is already tracked (every SaveAsync call — only CreateAsync
        // has a brand-new note), EF's implicit change detection sees the
        // client-assigned Guid key on a graph-discovered entity and marks it
        // Modified instead of Added, issuing an UPDATE for a row that doesn't
        // exist yet (0 rows affected -> DbUpdateConcurrencyException on every
        // single edit). db.NoteVersions.Add(...) states the intent directly.
        db.NoteVersions.Add(new NoteVersion
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            VersionNo = versionNo,
            S3Key = key,
            Filename = NoteContent.ExportFilename(note.EntryDate, title),
            ByteSize = byteSize,
            ContentHash = KeyHierarchyService.Sha256(Encoding.UTF8.GetBytes(body)),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Seals the file (fresh GCM nonce on every call) and PUTs it. Returns the ciphertext size.</summary>
    private async Task<int> PutVersionObjectAsync(
        Note note,
        string title,
        int versionNo,
        string key,
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
        await storage.PutAsync(key, encrypted, ct);
        return encrypted.Length;
    }

    private async Task<Note> LoadNoteAsync(Guid userId, Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes
            .Include(n => n.Versions)
            .FirstOrDefaultAsync(n => n.Id == noteId && n.UserId == userId, ct)
            ?? throw new NotFoundException("Note not found.");
        return note;
    }

    // Serialises writes per note. Without it two concurrent saves (e.g.
    // debounced autosave + blur save) both pass the revision check and both
    // PUT the same object key — the loser overwriting the winner's ciphertext
    // — or, across a freeze, the loser 500s on IX_note_versions_NoteId_VersionNo.
    // Taken before LoadNoteAsync so the waiter reads the winner's committed state.
    private async Task<IDbContextTransaction> LockNoteAsync(Guid userId, Guid noteId, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM notes WHERE "Id" = {noteId} AND "UserId" = {userId} FOR UPDATE""", ct);
        return tx;
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

public sealed class VersionConflictException(int currentVersion, string currentRevision)
    : ApiException(
        "urn:ai-diary:error:version-conflict",
        "The note was modified since you loaded it.",
        StatusCodes.Status409Conflict)
{
    public int CurrentVersion { get; } = currentVersion;
    public string CurrentRevision { get; } = currentRevision;
}
