using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Threading.RateLimiting;
using AiDiary.Api.Captcha;
using AiDiary.Api.Configuration;
using AiDiary.Api.Contracts;
using AiDiary.Api.Data;
using AiDiary.Api.Notes;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Services;

/// <summary>
/// The only endpoint strangers can reach carries the hardening budget:
/// captcha, rate limiting by IP (middleware) and by email (here), a message
/// cap, and no disclosure of whether an email is already registered.
/// </summary>
public sealed class AccessRequestService(
    AppDbContext db,
    ICaptchaVerifier captcha,
    MailService mail,
    DiaryOptions options,
    ILogger<AccessRequestService> logger)
{
    private static readonly ConcurrentDictionary<string, FixedWindowRateLimiter> EmailLimiters = new();

    public async Task SubmitAsync(AccessRequestRequest request, string? sourceIp, CancellationToken ct = default)
    {
        var email = (request.Email ?? "").Trim();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out _))
        {
            throw new ApiException("urn:ai-diary:error:invalid-email", "Enter a valid email address.", 400);
        }
        if (request.Message is { Length: > 500 })
        {
            throw new ApiException("urn:ai-diary:error:invalid-request", "The message is too long.", 400);
        }

        var emailKey = email.ToLowerInvariant();
        var limiter = EmailLimiters.GetOrAdd(emailKey, _ => new FixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                QueueLimit = 0,
                Window = TimeSpan.FromHours(1),
                AutoReplenishment = true,
            }));

        if (EmailLimiters.Count > 1000)
        {
            foreach (var stale in EmailLimiters.Keys.Where(k => !string.Equals(k, emailKey, StringComparison.Ordinal)).Take(200))
            {
                EmailLimiters.TryRemove(stale, out _);
            }
        }

        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            throw new ApiException("urn:ai-diary:error:rate-limited", "Too many requests. Try again later.", 429);
        }

        if (!await captcha.VerifyAsync(request.CaptchaToken ?? "", sourceIp, ct))
        {
            throw new ApiException("urn:ai-diary:error:captcha", "Captcha verification failed.", 400);
        }

        // Silent dedupe: nothing in the response may reveal whether the email
        // is already registered or already requested.
        if (await db.AccessRequests.AnyAsync(a => a.Email == email && a.Status == "pending", ct))
        {
            return;
        }

        db.AccessRequests.Add(new AccessRequest
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            Message = request.Message,
            Status = "pending",
            RequestedAt = DateTimeOffset.UtcNow,
            SourceIp = IPAddress.TryParse(sourceIp, out var ip) ? ip : null,
        });
        await db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(options.AdminEmail))
        {
            logger.LogWarning("Access request received but DIARY_ADMIN_EMAIL is not set; nobody was notified.");
            return;
        }
        await mail.SendAsync(
            options.AdminEmail,
            "AI Diary: new access request",
            $"A new access request arrived.\n\nEmail: {email}\nMessage: {request.Message ?? "(none)"}",
            ct);
    }
}
