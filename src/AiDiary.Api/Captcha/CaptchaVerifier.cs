using System.Text.Json;
using AiDiary.Api.Configuration;

namespace AiDiary.Api.Captcha;

public interface ICaptchaVerifier
{
    Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken ct = default);
}

/// <summary>
/// Dispatches to Turnstile or hCaptcha based on DIARY_CAPTCHA_PROVIDER.
/// "none" accepts everything (development and local testing).
/// </summary>
public sealed class CaptchaVerifier(HttpClient http, DiaryOptions options, ILogger<CaptchaVerifier> logger) : ICaptchaVerifier
{
    public async Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken ct = default)
    {
        var provider = options.CaptchaProvider;
        if (provider == "none" || string.IsNullOrEmpty(options.CaptchaSecret))
        {
            return true;
        }

        var verifyUrl = provider switch
        {
            "turnstile" => "https://challenges.cloudflare.com/turnstile/v0/siteverify",
            "hcaptcha" => "https://api.hcaptcha.com/siteverify",
            _ => throw new InvalidOperationException($"Unknown captcha provider '{provider}'."),
        };

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = options.CaptchaSecret,
            ["response"] = token,
            ["remoteip"] = remoteIp ?? "",
        });
        // Fail closed: an unreachable or misbehaving provider counts as a
        // failed verification (400), not an unhandled 500.
        try
        {
            using var response = await http.PostAsync(verifyUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Captcha provider {Provider} returned {StatusCode}.", provider, (int)response.StatusCode);
                return false;
            }
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Captcha provider {Provider} could not be reached.", provider);
            return false;
        }
    }
}
