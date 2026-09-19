using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiDiary.Api.Configuration;
using AiDiary.Api.Notes;

namespace AiDiary.Api.Keycloak;

/// <summary>
/// Thin client for the Keycloak Admin API: client-credentials token, user
/// creation, role assignment and the execute-actions email. Keycloak owns
/// credentials and email flows; the application never sees a password.
/// </summary>
public sealed class KeycloakAdminClient(HttpClient http, DiaryOptions options)
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt;

    private string Realm => options.OidcAuthority.TrimEnd('/').Split('/').Last();

    private string AdminBase => options.OidcAuthority.TrimEnd('/') + "/admin/realms/" + Realm;

    public async Task<string> CreateUserAsync(string email, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Post, AdminBase + "/users")
        {
            Content = JsonContent.Create(new
            {
                username = email,
                email,
                enabled = true,
                emailVerified = false,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                "urn:ai-diary:error:keycloak",
                $"Keycloak user creation failed ({response.StatusCode}).",
                StatusCodes.Status502BadGateway);
        }
        var location = response.Headers.Location?.ToString() ?? "";
        var id = location.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s))
            ?? throw new ApiException("urn:ai-diary:error:keycloak", "Keycloak did not return a user id.", 502);
        return id;
    }

    public async Task AssignRealmRoleAsync(string userId, string roleName, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var roleRequest = new HttpRequestMessage(HttpMethod.Get, AdminBase + $"/roles/{roleName}");
        roleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var roleResponse = await http.SendAsync(roleRequest, ct);
        if (!roleResponse.IsSuccessStatusCode)
        {
            throw new ApiException("urn:ai-diary:error:keycloak", $"Keycloak role '{roleName}' lookup failed.", 502);
        }
        var role = await roleResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

        var assignRequest = new HttpRequestMessage(HttpMethod.Post, AdminBase + $"/users/{userId}/role-mappings/realm")
        {
            Content = JsonContent.Create(new[] { role }),
        };
        assignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var assignResponse = await http.SendAsync(assignRequest, ct);
        if (!assignResponse.IsSuccessStatusCode)
        {
            throw new ApiException("urn:ai-diary:error:keycloak", $"Assigning role '{roleName}' failed.", 502);
        }
    }

    /// <summary>VERIFY_EMAIL + UPDATE_PASSWORD + UPDATE_PROFILE, valid 72 hours.</summary>
    public async Task SendExecuteActionsEmailAsync(string userId, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            AdminBase + $"/users/{userId}/execute-actions-email?lifespan=259200")
        {
            Content = JsonContent.Create(new[] { "VERIFY_EMAIL", "UPDATE_PASSWORD", "UPDATE_PROFILE" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                "urn:ai-diary:error:keycloak",
                $"Sending the account setup email failed ({response.StatusCode}).",
                StatusCodes.Status502BadGateway);
        }
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Delete, AdminBase + $"/users/{userId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await http.SendAsync(request, ct);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _cachedToken;
        }
        await TokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _cachedToken;
            }
            var tokenUrl = options.OidcAuthority.TrimEnd('/') + "/protocol/openid-connect/token";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.OidcClientId,
                ["client_secret"] = options.OidcClientSecret,
            });
            var response = await http.PostAsync(tokenUrl, content, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _cachedToken = result.GetProperty("access_token").GetString();
            var expiresIn = result.GetProperty("expires_in").GetDouble();
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _cachedToken ?? "";
        }
        finally
        {
            TokenLock.Release();
        }
    }
}
