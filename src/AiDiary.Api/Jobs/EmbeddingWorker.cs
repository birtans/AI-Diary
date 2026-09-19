using System.Text;
using System.Text.Json;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Embeddings;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AiDiary.Api.Jobs;

/// <summary>
/// Background embedding generation. Polls the Postgres job table every few
/// seconds; a cold model load can take seconds and no save should wait on it.
/// </summary>
public sealed class EmbeddingWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<EmbeddingWorker> logger) : BackgroundService
{
    private const int MaxInputChars = 32_000; // ~8000 tokens, bge-m3 has 8192-token context

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedding worker pass failed.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ObjectStorage>();
        var crypto = scope.ServiceProvider.GetRequiredService<KeyHierarchyService>();
        var embeddings = scope.ServiceProvider.GetRequiredService<OllamaEmbeddingService>();

        // Recover jobs orphaned by a previous crash.
        await db.BackgroundJobs
            .Where(j => j.Status == "running")
            .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Status, "pending"), ct);

        var jobs = await db.BackgroundJobs
            .Where(j => j.Status == "pending" && j.RunAt <= DateTimeOffset.UtcNow)
            .OrderBy(j => j.RunAt)
            .Take(5)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            job.Status = "running";
        }
        if (jobs.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        foreach (var job in jobs)
        {
            try
            {
                await ExecuteAsync(job, db, storage, crypto, embeddings, ct);
                job.Status = "done";
                job.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                job.Status = "pending";
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                job.Attempts++;
                if (job.Attempts >= 6)
                {
                    job.Status = "failed";
                    job.LastError = Truncate(ex.Message);
                }
                else
                {
                    job.Status = "pending";
                    job.RunAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, job.Attempts));
                    job.LastError = Truncate(ex.Message);
                }
                logger.LogWarning("Embedding job {Kind} {JobId} attempt {Attempts} failed.", job.Kind, job.Id, job.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ExecuteAsync(
        BackgroundJob job,
        AppDbContext db,
        ObjectStorage storage,
        KeyHierarchyService crypto,
        OllamaEmbeddingService embeddings,
        CancellationToken ct)
    {
        if (job.Kind != JobQueue.KindEmbedNote)
        {
            job.Status = "failed";
            job.LastError = $"Unknown job kind '{job.Kind}'.";
            return;
        }

        using var payload = JsonDocument.Parse(job.Payload);
        var noteId = payload.RootElement.GetProperty("note_id").GetGuid();

        var note = await db.Notes.Include(n => n.Versions)
            .FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null || note.DeletedAt is not null)
        {
            return; // purged or trashed; nothing to embed
        }

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == note.UserId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
        var version = note.Versions.First(v => v.VersionNo == note.CurrentVersion);

        var file = await storage.GetAsync(version.S3Key, ct);
        var plaintext = AiDiary.Crypto.FileEnvelope.Open(file, dek);
        var (_, body) = NoteContent.Split(Encoding.UTF8.GetString(plaintext));
        var title = crypto.DecryptText(ukek, note.TitleEnc);

        var input = (title + "\n\n" + body).Trim();
        if (input.Length > MaxInputChars)
        {
            input = input[..MaxInputChars];
        }

        var vector = await embeddings.EmbedAsync(input, ct);

        var row = await db.NoteEmbeddings.FirstOrDefaultAsync(e => e.NoteId == note.Id, ct);
        if (row is null)
        {
            row = new NoteEmbedding { NoteId = note.Id, UserId = note.UserId };
            db.NoteEmbeddings.Add(row);
        }
        row.Embedding = new Vector(vector);
        row.Model = "bge-m3";
        row.GeneratedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];
}
