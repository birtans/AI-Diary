using AiDiary.Api.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiDiary.Api.Services;

/// <summary>
/// Optional SMTP for admin notifications. Unconfigured = log only.
/// MailKit rather than System.Net.Mail, which only speaks STARTTLS and hangs
/// on implicit-TLS port 465 (what Resend and most providers recommend).
/// </summary>
public sealed class MailService(DiaryOptions options, ILogger<MailService> logger)
{
    private const int TimeoutMs = 15_000;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.SmtpHost) && !string.IsNullOrWhiteSpace(options.SmtpFrom);

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            logger.LogWarning(
                "SMTP is not configured; notification to {AdminEmail} was not sent. Subject: {Subject}.",
                options.AdminEmail, subject);
            return;
        }

        // 465 is TLS from the first byte. Elsewhere STARTTLS is mandatory once
        // a password would cross the wire, and opportunistic otherwise (the
        // dev Mailpit on 1025 offers no TLS at all).
        var hasCredentials = !string.IsNullOrWhiteSpace(options.SmtpUsername);
        var security = options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
            : hasCredentials ? SecureSocketOptions.StartTls
            : SecureSocketOptions.StartTlsWhenAvailable;

        // Building the message is inside the try too: a malformed
        // DIARY_SMTP_FROM or recipient throws here, and callers have usually
        // already committed their work — a notification must never fail them.
        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(options.SmtpFrom));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient { Timeout = TimeoutMs };
            await client.ConnectAsync(options.SmtpHost, options.SmtpPort, security, ct);
            if (hasCredentials)
            {
                await client.AuthenticateAsync(options.SmtpUsername, options.SmtpPassword, ct);
            }
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification email.");
        }
    }
}
