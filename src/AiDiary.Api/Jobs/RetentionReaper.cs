using AiDiary.Api.Configuration;
using AiDiary.Api.Data;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Jobs;

/// <summary>
/// Nightly retention. Runs at 03:00 UTC and once on startup. Deletes are
/// B2-first, then Postgres, so a crash leaves an orphaned row rather than a
/// dangling object. Also re-queues stale embeddings.
/// </summary>
public sealed class RetentionReaper(
    IServiceScopeFactory scopeFactory,
    DiaryOptions options,
    ILogger<RetentionReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = UntilNextRun();
            logger.LogInformation("Retention reaper next run in {Delay:g}.", delay);
            await Task.Delay(delay, stoppingToken);
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention reaper run failed.");
            }
        }
    }

    private static TimeSpan UntilNextRun()
    {
        var now = DateTimeOffset.UtcNow;
        var next = now.Date.AddHours(3);
        if (next <= now)
        {
            next = next.AddDays(1);
        }
        return next - now;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ObjectStorage>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobQueue>();

        var now = DateTimeOffset.UtcNow;

        // 1. Superseded versions older than the retention window.
        var versionCutoff = now.AddDays(-options.VersionRetentionDays);
        var oldVersions = await db.NoteVersions
            .Where(v => v.SupersededAt != null && v.SupersededAt < versionCutoff)
            .ToListAsync(ct);
        if (oldVersions.Count > 0)
        {
            await storage.DeleteManyAsync(oldVersions.Select(v => v.S3Key), ct);
            db.NoteVersions.RemoveRange(oldVersions);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Reaped {Count} superseded note versions.", oldVersions.Count);
        }

        // 2. Soft-deleted notes past the trash window: every version, the
        //    note row and the embedding.
        var trashCutoff = now.AddDays(-options.TrashRetentionDays);
        var deadNotes = await db.Notes
            .Where(n => n.DeletedAt != null && n.DeletedAt < trashCutoff)
            .Include(n => n.Versions)
            .Include(n => n.Embedding)
            .ToListAsync(ct);
        foreach (var note in deadNotes)
        {
            if (note.Versions.Count > 0)
            {
                await storage.DeleteManyAsync(note.Versions.Select(v => v.S3Key), ct);
            }
            db.NoteVersions.RemoveRange(note.Versions);
            if (note.Embedding is not null)
            {
                db.NoteEmbeddings.Remove(note.Embedding);
            }
            db.Notes.Remove(note);
        }
        if (deadNotes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} trashed notes.", deadNotes.Count);
        }

        // 3. Conversations idle past the window.
        var conversationCutoff = now.AddDays(-options.ConversationRetentionDays);
        var deadConversations = await db.Conversations
            .Where(c => c.LastMessageAt < conversationCutoff)
            .Include(c => c.Messages)
            .ToListAsync(ct);
        db.Messages.RemoveRange(deadConversations.SelectMany(c => c.Messages));
        db.Conversations.RemoveRange(deadConversations);
        if (deadConversations.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} conversations.", deadConversations.Count);
        }

        // 4. Access request source IPs: nulled after 90 days, rows kept.
        var ipCutoff = now.AddDays(-90);
        var staleRequests = await db.AccessRequests
            .Where(a => a.SourceIp != null && a.RequestedAt < ipCutoff)
            .ToListAsync(ct);
        foreach (var request in staleRequests)
        {
            request.SourceIp = null;
        }
        if (staleRequests.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // 5. Re-queue stale embeddings (generated_at older than updated_at,
        //    or missing entirely).
        var staleNotes = await db.Notes
            .Where(n => n.DeletedAt == null && (n.Embedding == null || n.Embedding.GeneratedAt < n.UpdatedAt))
            .Select(n => n.Id)
            .ToListAsync(ct);
        foreach (var noteId in staleNotes)
        {
            var payload = new { note_id = noteId };
            if (!await jobs.IsPendingAsync(JobQueue.KindEmbedNote, payload, ct))
            {
                await jobs.EnqueueAsync(JobQueue.KindEmbedNote, payload, ct: ct);
            }
        }
        if (staleNotes.Count > 0)
        {
            logger.LogInformation("Re-queued {Count} stale embeddings.", staleNotes.Count);
        }
    }
}
