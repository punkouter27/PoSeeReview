namespace PoSeeReview.Mobile.Services;

public sealed class MobileConfig
{
    private const string BaseUrlKey = "poseereview_api_base_url";
    private readonly IPreferences _preferences = Preferences.Default;

    public string BaseUrl
    {
        get => _preferences.Get(BaseUrlKey, GetDefaultBaseUrl());
        set => _preferences.Set(BaseUrlKey, NormalizeUrl(value));
    }

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
