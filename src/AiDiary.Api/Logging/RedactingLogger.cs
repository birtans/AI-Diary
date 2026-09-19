using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiDiary.Api.Logging;

/// <summary>
/// Logging redaction. Structured logging must never emit note bodies, titles,
/// keys, presigned URLs or chat content — enforced here by property name so a
/// future careless LogInformation cannot leak them.
/// </summary>
public sealed partial class RedactingLogger(ILogger inner) : ILogger
{
    private static readonly HashSet<string> RedactedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "content", "title", "plaintext", "text",
        "masterkey", "master_key", "ukek", "wrapped_ukek", "wrappedukek",
        "dek", "wrapped_dek", "wrappeddek", "apikey", "api_key", "llm_api_key",
        "presignedurl", "presigned_url", "token", "secret", "password",
        "accesskey", "access_key", "secretkey", "secret_key",
    };

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        message = PresignedUrlRegex().Replace(message, "[REDACTED]");

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var redacted = pairs
                .Select(kv => RedactedKeys.Contains(kv.Key)
                    ? new KeyValuePair<string, object?>(kv.Key, "[REDACTED]")
                    : kv)
                .ToList();
            inner.Log(logLevel, eventId, redacted, exception, (_, _) => message);
            return;
        }
        inner.Log(logLevel, eventId, state, exception, (_, _) => message);
    }

    [GeneratedRegex(@"(?<url>https?://[^\s""'<>]*X-Amz[^\s""'<>]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PresignedUrlRegex();
}

public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void Dispose() => inner.Dispose();
}

public sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}
