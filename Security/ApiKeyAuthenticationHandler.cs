using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TranscriptionPipeline.Security;

/// <summary>
/// Simple shared-secret auth for a service-to-service API like this one - the caller
/// sends an X-Api-Key header, compared against the configured key using a
/// constant-time comparison so response timing can't leak how much of the key matched.
/// The key itself comes from configuration (environment variable / user-secrets in
/// development, Key Vault in production) - never hard-coded or committed.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrEmpty(Options.ApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "API key authentication is not configured on the server."));
        }

        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out var provided)
            || string.IsNullOrWhiteSpace(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!IsValidKey(provided.ToString(), Options.ApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(ApiKeyAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsValidKey(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Lengths differing is itself not timing-sensitive information worth hiding here,
        // but the byte-for-byte comparison below is constant-time.
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
    }
}
