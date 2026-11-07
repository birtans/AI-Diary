using System.Text.Json;
using AiDiary.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Jobs;

/// <summary>Postgres-backed job queue. Single instance deployment; polled by the worker.</summary>
public sealed class JobQueue(AppDbContext db)
{
    public const string KindEmbedNote = "embed_note";

    public async Task EnqueueAsync(string kind, object payload, DateTimeOffset? runAt = null, CancellationToken ct = default)
    {
        db.BackgroundJobs.Add(new BackgroundJob
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Payload = JsonSerializer.Serialize(payload),
            Status = "pending",
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            RunAt = runAt ?? DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Enqueues unless an identical job is still waiting to start. A running
    /// job doesn't count: it may already have read older content.
    /// </summary>
    public async Task<bool> EnqueueUnlessPendingAsync(string kind, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        if (await db.BackgroundJobs.AnyAsync(j => j.Kind == kind && j.Payload == json && j.Status == "pending", ct))
        {
            return false;
        }
        await EnqueueAsync(kind, payload, ct: ct);
        return true;
    }

    /// <summary>Drops jobs for a payload that haven't started yet.</summary>
    public Task CancelPendingAsync(string kind, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        return db.BackgroundJobs
            .Where(j => j.Kind == kind && j.Payload == json && j.Status == "pending")
            .ExecuteDeleteAsync(ct);
    }
}
