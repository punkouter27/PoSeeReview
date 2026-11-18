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

    public static string RedirectUri => "poseereview://auth";

    public static string GetDefaultBaseUrl() =>
        DeviceInfo.Current.Platform == DevicePlatform.Android
            ? "http://10.0.2.2:5000"
            : "https://localhost:5001";

    private static string NormalizeUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed) ? GetDefaultBaseUrl() : trimmed;
    }
}
