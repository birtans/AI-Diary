using AiDiary.Api.Configuration;
using AiDiary.Api.Data;
using AiDiary.Api.Notes;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Jobs;

/// <summary>
/// Nightly run at DIARY_NIGHTLY_RUN_TIME in DIARY_NIGHTLY_RUN_TZ (02:00
/// Europe/Amsterdam by default). Retention deletes are B2-first, then
/// Postgres, so a crash leaves an orphaned row rather than a dangling object.
/// Then saves a version of every note written since the previous run and
/// queues indexing for every note whose vectors are out of date.
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

    private TimeSpan UntilNextRun()
    {
        var zone = ResolveZone();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var next = NextRunUtc(today, options.NightlyRunTime, zone);
        if (next <= now)
        {
            next = NextRunUtc(today.AddDays(1), options.NightlyRunTime, zone);
        }
        return next - now;
    }

    /// <summary>
    /// The run time on a local date in UTC. A time skipped by a spring-forward
    /// change moves an hour later; an ambiguous fall-back time resolves to
    /// standard time, so the run still happens once.
    /// </summary>
    internal static DateTimeOffset NextRunUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private TimeZoneInfo ResolveZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(options.NightlyRunTimezone, out var zone))
        {
            return zone;
        }
        logger.LogWarning("Unknown timezone {Timezone} for the nightly run; using UTC.", options.NightlyRunTimezone);
        return TimeZoneInfo.Utc;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ObjectStorage>();
        var notes = scope.ServiceProvider.GetRequiredService<NoteService>();

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
            .Include(n => n.Embeddings)
            .ToListAsync(ct);
        foreach (var note in deadNotes)
        {
            if (note.Versions.Count > 0)
            {
                await storage.DeleteManyAsync(note.Versions.Select(v => v.S3Key), ct);
            }
            db.NoteVersions.RemoveRange(note.Versions);
            db.NoteEmbeddings.RemoveRange(note.Embeddings);
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

        // 5. Version notes written since the previous run (25 hours covers the
        //    25-hour fall-back night; overlap is harmless, an already-retained
        //    text is never frozen twice) and index every stale note.
        var writtenAfter = now.AddHours(-25);
        var candidates = await db.Notes
            .AsNoTracking()
            .Where(n => n.DeletedAt == null
                && (n.IndexedContentAt == null
                    || n.IndexedContentAt != n.ContentChangedAt
                    || n.Versions.Any(v => v.VersionNo == n.CurrentVersion && v.CreatedAt > writtenAfter)))
            .Select(n => new { n.Id, n.UserId })
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            try
            {
                await notes.NightlyVersionAndIndexAsync(candidate.UserId, candidate.Id, writtenAfter, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Nightly version/index of note {NoteId} failed.", candidate.Id);
            }
            finally
            {
                // One context for the whole loop: don't let a failed note's
                // half-tracked state leak into the next note's SaveChanges.
                db.ChangeTracker.Clear();
            }
        }
        if (candidates.Count > 0)
        {
            logger.LogInformation("Nightly run versioned/indexed up to {Count} notes.", candidates.Count);
        }
    }
}
