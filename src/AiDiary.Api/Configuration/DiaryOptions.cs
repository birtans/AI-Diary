namespace AiDiary.Api.Configuration;

/// <summary>
/// All environment-driven configuration. Variable names follow the build
/// specification (DIARY_*). Loaded once at startup; the master key is read
/// lazily so design-time tooling never fails on a missing variable.
/// </summary>
public sealed class DiaryOptions
{
    public string MasterKeyEnv { get; init; } = "DIARY_MASTER_KEY";
    public required string DbConnection { get; init; }
    public required string S3Endpoint { get; init; }
    public string S3PublicEndpoint { get; init; } = "";
    public required string S3Region { get; init; }
    public required string S3Bucket { get; init; }
    public required string S3AccessKey { get; init; }
    public required string S3SecretKey { get; init; }
    public bool S3ForcePathStyle { get; init; }
    public int S3PresignTtlSeconds { get; init; }
    public required string OllamaUrl { get; init; }
    public string EmbedModel { get; init; } = "bge-m3";
    public required string OidcAuthority { get; init; }
    public string OidcInternalUrl { get; init; } = "";
    public string OidcClientId { get; init; } = "diary-api";
    public string OidcClientSecret { get; init; } = "";
    public string OidcAudience { get; init; } = "diary-web";

    /// <summary>
    /// Base URL the API uses to reach Keycloak (metadata discovery, admin API).
    /// Defaults to the authority base when not set — correct for host runs,
    /// overridden to http://keycloak:8080 inside the Docker network.
    /// </summary>
    public string OidcInternalBase
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OidcInternalUrl))
            {
                return OidcInternalUrl.TrimEnd('/');
            }
            var authority = OidcAuthority.TrimEnd('/');
            var index = authority.LastIndexOf("/realms/", StringComparison.Ordinal);
            return index > 0 ? authority[..index] : authority;
        }
    }
    /// <summary>
    /// Endpoint used to sign presigned URLs the browser fetches note
    /// ciphertext from directly. Defaults to S3Endpoint (correct when it's
    /// already publicly reachable, e.g. B2) — overridden in Docker dev where
    /// the API reaches MinIO via the internal hostname but the browser needs
    /// the published host port.
    /// </summary>
    public string S3PublicEndpointOrDefault =>
        string.IsNullOrWhiteSpace(S3PublicEndpoint) ? S3Endpoint : S3PublicEndpoint;

    public string AdminEmail { get; init; } = "";
    public string CaptchaProvider { get; init; } = "none";
    public string CaptchaSecret { get; init; } = "";
    public string CaptchaSiteKey { get; init; } = "";
    public int VersionRetentionDays { get; init; } = 90;
    public int TrashRetentionDays { get; init; } = 30;
    public int ConversationRetentionDays { get; init; } = 30;
    public string CorsOrigins { get; init; } = "http://localhost:5173,http://localhost:8081";
    public string BaseUrl { get; init; } = "http://localhost:8080";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public string SmtpUsername { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string SmtpFrom { get; init; } = "";

    public string[] CorsOriginList =>
        CorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static DiaryOptions Load()
    {
        string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        return new DiaryOptions
        {
            DbConnection = Env("DIARY_DB_CONNECTION", "Host=localhost;Port=5432;Database=diary;Username=diary;Password=diary"),
            S3Endpoint = Env("DIARY_S3_ENDPOINT", "http://localhost:9000"),
            S3PublicEndpoint = Env("DIARY_S3_PUBLIC_ENDPOINT", ""),
            S3Region = Env("DIARY_S3_REGION", "eu-central-1"),
            S3Bucket = Env("DIARY_S3_BUCKET", "diary"),
            S3AccessKey = Env("DIARY_S3_ACCESS_KEY", "minioadmin"),
            S3SecretKey = Env("DIARY_S3_SECRET_KEY", "minioadmin"),
            S3ForcePathStyle = Env("DIARY_S3_FORCE_PATH_STYLE", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            S3PresignTtlSeconds = int.TryParse(Env("DIARY_S3_PRESIGN_TTL_SECONDS", "300"), out var ttl) ? ttl : 300,
            OllamaUrl = Env("DIARY_OLLAMA_URL", "http://localhost:11434"),
            EmbedModel = Env("DIARY_EMBED_MODEL", "bge-m3"),
            OidcAuthority = Env("DIARY_OIDC_AUTHORITY", "http://localhost:8082/realms/diary"),
            OidcInternalUrl = Env("DIARY_OIDC_INTERNAL_URL", ""),
            OidcClientId = Env("DIARY_OIDC_CLIENT_ID", "diary-api"),
            OidcClientSecret = Env("DIARY_OIDC_CLIENT_SECRET", ""),
            OidcAudience = Env("DIARY_OIDC_AUDIENCE", "diary-web"),
            AdminEmail = Env("DIARY_ADMIN_EMAIL", ""),
            CaptchaProvider = Env("DIARY_CAPTCHA_PROVIDER", "none").ToLowerInvariant(),
            CaptchaSecret = Env("DIARY_CAPTCHA_SECRET", ""),
            CaptchaSiteKey = Env("DIARY_CAPTCHA_SITE_KEY", ""),
            VersionRetentionDays = int.TryParse(Env("DIARY_VERSION_RETENTION_DAYS", "90"), out var vd) ? vd : 90,
            TrashRetentionDays = int.TryParse(Env("DIARY_TRASH_RETENTION_DAYS", "30"), out var td) ? td : 30,
            ConversationRetentionDays = int.TryParse(Env("DIARY_CONVERSATION_RETENTION_DAYS", "30"), out var cd) ? cd : 30,
            CorsOrigins = Env("DIARY_CORS_ORIGINS", "http://localhost:5173,http://localhost:8081"),
            BaseUrl = Env("DIARY_BASE_URL", "http://localhost:8080"),
            SmtpHost = Env("DIARY_SMTP_HOST", ""),
            SmtpPort = int.TryParse(Env("DIARY_SMTP_PORT", "587"), out var sp) ? sp : 587,
            SmtpUsername = Env("DIARY_SMTP_USERNAME", ""),
            SmtpPassword = Env("DIARY_SMTP_PASSWORD", ""),
            SmtpFrom = Env("DIARY_SMTP_FROM", ""),
        };
    }
}
