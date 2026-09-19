using AiDiary.Api.Admin;
using AiDiary.Api.Auth;
using AiDiary.Api.Captcha;
using AiDiary.Api.Chat;
using AiDiary.Api.Configuration;
using AiDiary.Api.Controllers;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Embeddings;
using AiDiary.Api.Health;
using AiDiary.Api.Jobs;
using AiDiary.Api.Keycloak;
using AiDiary.Api.Logging;
using AiDiary.Api.Notes;
using AiDiary.Api.Search;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Threading.RateLimiting;

var options = DiaryOptions.Load();
var isSeedCommand = args.Contains("seed");

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------- services
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MasterKeyProvider>();
builder.Services.AddSingleton<ObjectStorage>();
builder.Services.AddSingleton<RecoveryKeyCache>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<ICaptchaVerifier, CaptchaVerifier>();
builder.Services.AddScoped<IAuthorizationHandler, AdminHandler>();

builder.Services.AddScoped<KeyHierarchyService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<NoteService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<AccessRequestService>();
builder.Services.AddScoped<RecoveryService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<Seeder>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<OllamaEmbeddingService>();
builder.Services.AddHttpClient<KeycloakAdminClient>();
builder.Services.AddHttpClient<CaptchaVerifier>();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(options.DbConnection, npgsql => npgsql.UseVector()));

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddProblemDetails();

// -------------------------------------------------------------- auth
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var authority = options.OidcAuthority.TrimEnd('/');
        var realm = authority.Split('/').Last();
        o.Authority = authority; // issuer validation uses what the browser sees
        o.MetadataAddress = options.OidcInternalBase + "/realms/" + realm + "/.well-known/openid-configuration";
        o.RequireHttpsMetadata =
            authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            options.OidcInternalBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        o.MapInboundClaims = false;
        o.TokenValidationParameters.ValidAudience = options.OidcAudience;
        // The discovery document is fetched via the internal URL, so its
        // "issuer" field is the internal address — but tokens carry the
        // browser-facing issuer. Pin ValidIssuer explicitly so the mismatch
        // doesn't fail validation (the internal issuer from metadata is
        // still accepted too, since the handler appends it to ValidIssuers).
        o.TokenValidationParameters.ValidIssuer = authority;
        o.TokenValidationParameters.NameClaimType = "preferred_username";
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.AddRequirements(new AdminRequirement()));
});

// --------------------------------------------------------- rate limiting
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public-access-request", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                QueueLimit = 0,
                Window = TimeSpan.FromHours(1),
                AutoReplenishment = true,
            }));
});

// ---------------------------------------------------------------- health
builder.Services.AddHealthChecks()
    .AddCheck<DbHealthCheck>("postgres")
    .AddCheck<StorageHealthCheck>("storage")
    .AddCheck<OllamaHealthCheck>("ollama");

// ------------------------------------------------------------------ cors
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(options.CorsOriginList)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// -------------------------------------------------------- hosted services
builder.Services.AddHostedService<EmbeddingWorker>();
builder.Services.AddHostedService<RetentionReaper>();

// -------------------------------------------------------------- logging
builder.Logging.ClearProviders();
builder.Services.AddSingleton<ILoggerFactory>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var inner = LoggerFactory.Create(b =>
    {
        b.AddConfiguration(configuration.GetSection("Logging"));
        b.AddSimpleConsole(c =>
        {
            c.SingleLine = true;
            c.TimestampFormat = "HH:mm:ss ";
        });
    });
    return new RedactingLoggerFactory(inner);
});

var app = builder.Build();

// ---------------------------------------------------------------- migrate
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<ObjectStorage>().EnsureBucketAsync();
}

// ------------------------------------------------------------------ seed
if (isSeedCommand)
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<Seeder>();
    await seeder.RunAsync(CancellationToken.None);
    Console.WriteLine("Seed completed.");
    return;
}

// -------------------------------------------------------------- pipeline
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = StatusCodes.Status500InternalServerError;
    var type = "urn:ai-diary:error:internal";
    var title = "An unexpected error occurred.";

    switch (ex)
    {
        case VersionConflictException versionConflict:
            status = versionConflict.Status;
            type = versionConflict.Type;
            title = versionConflict.Message;
            context.Response.Headers["X-Current-Version"] = versionConflict.CurrentVersion.ToString();
            break;
        case ApiException api:
            status = api.Status;
            type = api.Type;
            title = api.Message;
            break;
        case UnauthorizedAccessException:
            status = StatusCodes.Status401Unauthorized;
            type = "urn:ai-diary:error:unauthorized";
            title = "Authentication required.";
            break;
        case BadHttpRequestException:
            status = StatusCodes.Status400BadRequest;
            type = "urn:ai-diary:error:bad-request";
            title = "The request could not be read.";
            break;
        default:
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ExceptionHandler")
                .LogError(ex, "Unhandled exception.");
            break;
    }

    var problem = new ProblemDetails
    {
        Status = status,
        Type = type,
        Title = title,
        Instance = context.Request.Path,
    };
    if (ex is VersionConflictException conflict)
    {
        problem.Extensions["currentVersion"] = conflict.CurrentVersion;
    }
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(problem);
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    AllowCachingResponses = false,
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description }),
        }));
    },
});

app.Run();
