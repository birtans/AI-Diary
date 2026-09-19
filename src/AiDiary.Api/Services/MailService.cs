using System.Net;
using System.Net.Mail;
using AiDiary.Api.Configuration;

namespace AiDiary.Api.Services;

/// <summary>Optional SMTP for admin notifications. Unconfigured = log only.</summary>
public sealed class MailService(DiaryOptions options, ILogger<MailService> logger)
{
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

        using var message = new MailMessage(options.SmtpFrom, to, subject, body);
        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.SmtpPort != 25,
        };
        if (!string.IsNullOrWhiteSpace(options.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(options.SmtpUsername, options.SmtpPassword);
        }
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification email.");
        }
    }
}
