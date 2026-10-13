using System.Text.Json;
using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Keycloak;
using AiDiary.Api.Notes;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Admin;

public sealed class AdminService(
    AppDbContext db,
    KeycloakAdminClient keycloak,
    MailService mail,
    ObjectStorage storage,
    RevokedSubjects revoked,
    RecoveryKeyCache recoveryKeys,
    ILogger<AdminService> logger)
{
    public async Task<List<AccessRequestItem>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = db.AccessRequests.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(a => a.Status == status);
        }
        var rows = await query.OrderByDescending(a => a.RequestedAt).Take(200).ToListAsync(ct);
        return rows.Select(a => new AccessRequestItem(a.Id, a.Email, a.Message, a.Status, a.RequestedAt)).ToList();
    }

    public async Task ApproveAsync(Guid id, CancellationToken ct = default)
    {
        var request = await db.AccessRequests.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Access request not found.");
        if (request.Status != "pending")
        {
            throw new ConflictException("This request was already decided.");
        }

        var keycloakUserId = await keycloak.CreateUserAsync(request.Email, ct: ct);
        try
        {
            await keycloak.AssignRealmRoleAsync(keycloakUserId, "diary-user", ct);
        }
        catch
        {
            try
            {
                await keycloak.DeleteUserAsync(keycloakUserId, CancellationToken.None);
            }
            catch (ApiException)
            {
                // Best-effort rollback; surface the original failure.
            }
            throw;
        }

        // The application row exists from the moment of approval, even if the
        // invitation is never accepted. Keys are generated at first login.
        var appUser = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email, ct);
        if (appUser is null)
        {
            db.Users.Add(new AppUser
            {
                Id = Guid.CreateVersion7(),
                KeycloakSub = keycloakUserId,
                Email = request.Email,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        request.Status = "approved";
        request.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await keycloak.SendExecuteActionsEmailAsync(keycloakUserId, ct);
    }

    public async Task RejectAsync(Guid id, CancellationToken ct = default)
    {
        var request = await db.AccessRequests.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Access request not found.");
        if (request.Status != "pending")
        {
            throw new ConflictException("This request was already decided.");
        }
        request.Status = "rejected";
        request.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await mail.SendAsync(
            request.Email,
            "AI Diary: access request",
            "Thank you for your interest in AI Diary. Your request was not approved at this time.");
    }

    public async Task<List<AdminUserItem>> ListUsersAsync(Guid currentUserId, CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new AdminUserItem(u.Id, u.Email, u.IsAdmin, u.Notes.Count, u.CreatedAt, u.Id == currentUserId))
            .ToListAsync(ct);

    /// <summary>
    /// Deletes an account everywhere. Keycloak goes first, so the person loses
    /// access before their data does, and a failure part-way leaves the row in
    /// place for the admin to retry (an already-deleted Keycloak user is not an
    /// error). Then, in one transaction, storage before rows — as in the reaper
    /// — so a crash leaves a row rather than a dangling object.
    /// </summary>
    public async Task DeleteUserAsync(Guid adminId, Guid userId, CancellationToken ct = default)
    {
        if (userId == adminId)
        {
            throw new ConflictException("You cannot delete your own account.");
        }
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User not found.");

        await keycloak.DeleteUserAsync(user.KeycloakSub, ct);
        revoked.Add(user.KeycloakSub);
        recoveryKeys.Remove(userId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Waits out in-flight note saves, and holds off new notes (their FK
        // check on the user row) until the row is gone.
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM users WHERE "Id" = {userId} FOR UPDATE""", ct);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM notes WHERE "UserId" = {userId} FOR UPDATE""", ct);

        var noteIds = await db.Notes.Where(n => n.UserId == userId).Select(n => n.Id).ToListAsync(ct);

        // The whole prefix rather than the version rows' keys, so objects
        // orphaned by an earlier failed write go too.
        await storage.DeletePrefixAsync(ObjectStorage.UserPrefix(userId), ct);

        var jobPayloads = noteIds.Select(id => JsonSerializer.Serialize(new { note_id = id })).ToList();
        await db.BackgroundJobs.Where(j => jobPayloads.Contains(j.Payload)).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => m.Conversation.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.NoteEmbeddings.Where(e => e.Note.UserId == userId).ExecuteDeleteAsync(ct);
        await db.NoteVersions.Where(v => v.Note.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Notes.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AccessRequests.Where(a => a.Email == user.Email).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Admin {AdminId} deleted user {UserId} with {NoteCount} notes.", adminId, userId, noteIds.Count);
    }
}
