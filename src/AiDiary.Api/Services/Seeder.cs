using AiDiary.Api.Configuration;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Keycloak;
using AiDiary.Api.Notes;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Services;

/// <summary>
/// `dotnet AiDiary.Api.dll seed` — development convenience: creates the
/// admin application row (with keys), a handful of sample notes, and best-
/// effort a Keycloak user for the admin email.
/// </summary>
public sealed class Seeder(
    AppDbContext db,
    DiaryOptions options,
    KeyHierarchyService crypto,
    NoteService notes,
    KeycloakAdminClient keycloak,
    ILogger<Seeder> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var email = options.AdminEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogError("DIARY_ADMIN_EMAIL is not set; cannot seed the admin user.");
            return;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                KeycloakSub = "seed-admin-" + Guid.CreateVersion7(),
                Email = email,
                IsAdmin = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        user.IsAdmin = true;

        if (user.WrappedUkek is null || user.RecoveryWrappedUkek is null)
        {
            var ukek = crypto.CreateUkek();
            var key = crypto.GenerateRecoveryKey();
            user.WrappedUkek = crypto.WrapUkekUnderMaster(ukek);
            user.RecoveryWrappedUkek = crypto.WrapUkekUnderRecovery(ukek, key, user.Id);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Generated keys for admin user.");
        }

        // Point the application row at the real Keycloak user so an actual
        // login resolves to this row (the OIDC subject is the join key).
        try
        {
            // Dev seed: no SMTP here, so the email starts verified (the real
            // approval flow verifies via the execute-actions email instead).
            var keycloakId = await keycloak.CreateUserAsync(email, emailVerified: true, ct);
            if (!string.IsNullOrEmpty(keycloakId))
            {
                user.KeycloakSub = keycloakId;
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Created Keycloak user for admin email (password will be set via the console).");
        }
        catch (ApiException)
        {
            // Likely already exists (or transient): resolve by username.
            var existing = await keycloak.GetUserIdAsync(email, ct);
            if (!string.IsNullOrEmpty(existing))
            {
                user.KeycloakSub = existing;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Linked the admin row to the existing Keycloak user.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not create the Keycloak user: {Message}. Create it manually.", ex.Message);
        }

        if (!await db.Notes.AnyAsync(n => n.UserId == user.Id, ct))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await notes.CreateAsync(user.Id, new CreateNoteRequest(
                null, today.AddDays(-1), "Europe/Amsterdam",
                new NoteLocationRequest(52.1749, 5.0009, "Breukelen"), "user",
                "Rain on the canal\n\nCycled home in the rain and watched it fall on the water for a while. " +
                "Not everything needs to be productive."), ct);
            await notes.CreateAsync(user.Id, new CreateNoteRequest(
                null, today, null, null, "user",
                "First day with the new diary\n\nEverything is stored encrypted, indexed with pgvector " +
                "and searchable by meaning. This note is here to prove the pipeline works."), ct);
            await notes.CreateAsync(user.Id, new CreateNoteRequest(
                null, today.AddDays(-3), "Europe/Amsterdam", null, "user",
                "Reading list\n\nFinished the novel. Next up: something about the history of canals."), ct);
            logger.LogInformation("Created sample notes.");
        }
    }
}
