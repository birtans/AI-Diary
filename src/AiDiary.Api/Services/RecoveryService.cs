using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Notes;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Services;

/// <summary>
/// First-login recovery setup. Generates the UKEK and recovery key once,
/// returns the key exactly once, and verifies an acknowledgement by
/// attempting to unwrap the stored blob with the presented key (the AEAD tag
/// is the proof — the key itself is never stored in the clear).
/// </summary>
public sealed class RecoveryService(
    AppDbContext db,
    KeyHierarchyService crypto,
    RecoveryKeyCache cache)
{
    public async Task<RecoveryStatusResponse> StatusAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await LoadAsync(userId, ct);
        if (user.WrappedUkek is null || user.RecoveryWrappedUkek is null)
        {
            return new RecoveryStatusResponse("not_setup");
        }
        return user.RecoveryAcknowledgedAt is null
            ? new RecoveryStatusResponse("setup_pending_ack")
            : new RecoveryStatusResponse("ok");
    }

    public async Task<RecoverySetupResponse> SetupAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await LoadAsync(userId, ct);
        if (user.WrappedUkek is not null && user.RecoveryWrappedUkek is not null)
        {
            if (user.RecoveryAcknowledgedAt is not null)
            {
                throw new ConflictException("Recovery setup is already complete.");
            }
            throw new ConflictException(
                "Recovery was already set up for this account. If you lost the key, rotate it.");
        }

        var ukek = crypto.CreateUkek();
        var key = crypto.GenerateRecoveryKey();
        user.WrappedUkek = crypto.WrapUkekUnderMaster(ukek);
        user.RecoveryWrappedUkek = crypto.WrapUkekUnderRecovery(ukek, key, user.Id);
        await db.SaveChangesAsync(ct);

        cache.Put(user.Id, key);
        return new RecoverySetupResponse("setup_pending_ack", key, user.Id);
    }

    public async Task<RecoverySetupResponse> RotateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await LoadAsync(userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var key = crypto.GenerateRecoveryKey();
        user.RecoveryWrappedUkek = crypto.WrapUkekUnderRecovery(ukek, key, user.Id);
        user.RecoveryAcknowledgedAt = null;
        await db.SaveChangesAsync(ct);

        cache.Put(user.Id, key);
        return new RecoverySetupResponse("setup_pending_ack", key, user.Id);
    }

    public async Task AcknowledgeAsync(Guid userId, string key, CancellationToken ct = default)
    {
        var user = await LoadAsync(userId, ct);
        if (user.RecoveryWrappedUkek is not { Length: > 0 })
        {
            throw new ConflictException("Complete recovery setup first.");
        }
        var ukek = crypto.TryUnwrapUkekUnderRecovery(user.RecoveryWrappedUkek, key, user.Id);
        if (ukek is null)
        {
            throw new ApiException(
                "urn:ai-diary:error:invalid-recovery-key",
                "That recovery key does not match this account.",
                StatusCodes.Status400BadRequest);
        }
        user.RecoveryAcknowledgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        cache.Remove(user.Id);
    }

    public async Task<RecoveryExportResponse> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await LoadAsync(userId, ct);
        if (user.RecoveryWrappedUkek is not { Length: > 0 })
        {
            throw new ConflictException("Complete recovery setup first.");
        }
        return new RecoveryExportResponse(user.Id, Convert.ToBase64String(user.RecoveryWrappedUkek));
    }

    private async Task<AppUser> LoadAsync(Guid userId, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw new NotFoundException("User does not exist.");
}
