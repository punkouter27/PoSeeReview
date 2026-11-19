namespace PoSeeReview.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "poseereview_api_base_url";
    private const string ClientIdKey = "poseereview_entra_client_id";
    private const string TenantIdKey = "poseereview_entra_tenant_id";
    private const string ScopeKey = "poseereview_entra_scope";
    private readonly IPreferences _preferences = Preferences.Default;

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

    public string EntraClientId
    {
        get => _preferences.Get(ClientIdKey, string.Empty);
        set => _preferences.Set(ClientIdKey, (value ?? string.Empty).Trim());
    }

    public string EntraTenantId
    {
        get => _preferences.Get(TenantIdKey, "common");
        set => _preferences.Set(TenantIdKey, string.IsNullOrWhiteSpace(value) ? "common" : value.Trim());
    }

    public string EntraScope
    {
        get => _preferences.Get(ScopeKey, "openid profile email");
        set => _preferences.Set(ScopeKey, string.IsNullOrWhiteSpace(value) ? "openid profile email" : value.Trim());
    }

    /// <summary>The deployed host. App Links / Universal Links claim it; Release builds talk to it.</summary>
    public const string ProductionHost = "app-poseereview2.azurewebsites.net";

    /// <summary>Custom scheme: <c>poseereview://auth</c> (sign-in callback), <c>poseereview://comic/{id}</c>.</summary>
    public const string AppScheme = "poseereview";

    public static string RedirectUri => $"{AppScheme}://auth";

    // Debug: scripts/mobile-dev.ps1 runs `adb reverse tcp:5000 tcp:<api port>`, so localhost:5000 on
    // a USB phone or an emulator is the dev PC's API — no IP, firewall or certificate setup.
    // (Without adb reverse an emulator can still use http://10.0.2.2:5000 via Settings.)
    public static string GetDefaultBaseUrl() =>
#if DEBUG
        DeviceInfo.Current.Platform == DevicePlatform.Android
            ? "http://localhost:5000"
            : "https://localhost:5001";
#else
        $"https://{ProductionHost}";
#endif

    private static string NormalizeUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? GetDefaultBaseUrl() : trimmed;
    }
}
