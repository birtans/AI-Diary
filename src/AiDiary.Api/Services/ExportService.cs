using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Notes;
using AiDiary.Api.Storage;
using AiDiary.Crypto;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Services;

/// <summary>
/// Two exports. /api/export produces plain .md files; /api/export/encrypted
/// produces a directory the reference decryptors can turn into plain .md
/// with only a recovery key: the ciphertext objects exactly as stored,
/// keys.json (wrapped DEKs) and recovery.dat (recovery-wrapped UKEK).
/// </summary>
public sealed class ExportService(
    AppDbContext db,
    ObjectStorage storage,
    KeyHierarchyService crypto)
{
    private const string KitReadme = """
        AI Diary encrypted archive
        ==========================

        This directory can be decrypted with only your recovery key using the
        reference decryptor shipped with AI Diary:

          .NET   : dotnet run --project src/AiDiary.Decryptor -- --key YOUR-KEY --dir .
          Python : python3 tools/decryptor.py --key YOUR-KEY --dir .

        Contents:
          recovery.dat  your user id and the recovery-wrapped user key
          keys.json     the wrapped data key for every note below
          *.md.enc      your notes, exactly as stored in object storage

        Never store this directory and your recovery key in the same place.
        """;

    public async Task ExportPlainAsync(Guid userId, Stream outStream, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(outStream, ZipArchiveMode.Create, leaveOpen: true);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.UserId == userId && n.DeletedAt == null)
            .Include(n => n.Versions)
            .OrderByDescending(n => n.EntryDate)
            .ToListAsync(ct);

        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            var version = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
            var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
            var file = await storage.GetAsync(version.S3Key, ct);
            var plaintext = FileEnvelope.Open(file, dek);

            var entry = zip.CreateEntry(version.Filename, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(plaintext, ct);
        }
    }

    public async Task ExportEncryptedAsync(Guid userId, Stream outStream, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(outStream, ZipArchiveMode.Create, leaveOpen: true);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);

        if (user.RecoveryWrappedUkek is not { Length: > 0 })
        {
            throw new ConflictException("Recovery setup has not been completed for this account.");
        }

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.UserId == userId && n.DeletedAt == null)
            .Include(n => n.Versions)
            .OrderByDescending(n => n.EntryDate)
            .ToListAsync(ct);

        var keys = new KeysFile { UserId = user.Id, Notes = [] };
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            var version = note.Versions.First(v => v.VersionNo == note.CurrentVersion);
            var file = await storage.GetAsync(version.S3Key, ct);
            var entryName = version.Filename.EndsWith(".md", StringComparison.Ordinal)
                ? version.Filename + ".enc"
                : version.Filename + ".md.enc";

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using (var stream = entry.Open())
            {
                await stream.WriteAsync(file, ct);
            }
            keys.Notes.Add(new KeysFileNote
            {
                ObjectKey = version.S3Key,
                WrappedDek = Convert.ToBase64String(note.WrappedDek),
                Filename = entryName,
            });
        }

        var dat = new RecoveryDat
        {
            UserId = user.Id,
            RecoveryWrappedUkek = Convert.ToBase64String(user.RecoveryWrappedUkek),
        };

        await WriteTextEntryAsync(zip, "recovery.dat", JsonSerializer.Serialize(dat), ct);
        await WriteTextEntryAsync(zip, "keys.json", JsonSerializer.Serialize(keys), ct);
        await WriteTextEntryAsync(zip, "README.txt", KitReadme, ct);
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, ct);
    }
}
