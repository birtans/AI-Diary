using System.Text.Json;
using AiDiary.Api.Data;

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

    public Task<bool> IsPendingAsync(string kind, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        return Task.FromResult(db.BackgroundJobs.Any(j =>
            j.Kind == kind && j.Payload == json && (j.Status == "pending" || j.Status == "running")));
    }
}
