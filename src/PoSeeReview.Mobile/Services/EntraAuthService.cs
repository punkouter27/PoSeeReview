using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoSeeReview.Mobile.Services;

public sealed class EntraAuthService(MobileConfig config, SecureSessionStore sessionStore)
{
    public async Task<string> SignInWithPkceAsync(CancellationToken ct = default)
    {
        var clientId = config.EntraClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Enter an Entra Client ID first.");
        }

        var tenant = config.EntraTenantId;
        var redirectUri = MobileConfig.RedirectUri;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var scope = config.EntraScope;
        var authorizeUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_mode=query" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}";

        WebAuthenticatorResult authResult;
        try
        {
            authResult = await WebAuthenticator.Default.AuthenticateAsync(
                new Uri(authorizeUrl),
                new Uri(redirectUri),
                ct).ConfigureAwait(false);
        }
        catch (NotImplementedException)
        {
            throw new InvalidOperationException(
                "WebAuthenticator is unavailable on unpackaged Windows. Use Android/iOS or paste a JWT / X-Fake-User below.");
        }

        if (!authResult.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Authorization code missing from Entra callback.");
        }

        if (authResult.Properties.TryGetValue("state", out var returnedState)
            && !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        using var tokenClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = scope,
        });

        var tokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token";
        using var resp = await tokenClient.PostAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed ({(int)resp.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.TryGetProperty("id_token", out var idEl) && !string.IsNullOrWhiteSpace(idEl.GetString())
            ? idEl.GetString()
            : root.TryGetProperty("access_token", out var accessEl)
                ? accessEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("No token returned by Entra.");
        }

        await sessionStore.SetBearerTokenAsync(token).ConfigureAwait(false);
        return token;
    }

    public Task SignOutAsync() => sessionStore.SetBearerTokenAsync(null);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
