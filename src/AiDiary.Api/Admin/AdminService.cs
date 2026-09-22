using AiDiary.Api.Contracts;
using AiDiary.Api.Data;
using AiDiary.Api.Keycloak;
using AiDiary.Api.Notes;
using AiDiary.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Admin;

public sealed class AdminService(
    AppDbContext db,
    KeycloakAdminClient keycloak,
    MailService mail)
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
            await keycloak.DeleteUserAsync(keycloakUserId, CancellationToken.None);
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
}
