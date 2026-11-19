using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly MobileConfig _config;
    private readonly SecureSessionStore _store;
    private readonly EntraAuthService _entraAuth;
    private readonly PoSeeReviewMobileClient _client;
    private readonly Haptics _haptics;

    public SettingsPage(
        MobileConfig config,
        SecureSessionStore store,
        EntraAuthService entraAuth,
        PoSeeReviewMobileClient client,
        Haptics haptics)
    {
        _haptics = haptics;
        _config = config;
        _store = store;
        _entraAuth = entraAuth;
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        HapticsSwitch.IsToggled = _haptics.Enabled;
        BaseUrlEntry.Text = _config.BaseUrl;
        ClientIdEntry.Text = _config.EntraClientId;
        TenantIdEntry.Text = _config.EntraTenantId;
        ScopeEntry.Text = _config.EntraScope;
        RedirectLabel.Text = $"Redirect URI: {MobileConfig.RedirectUri}";
        FakeUserEntry.Text = await _store.GetFakeUserAsync();
        TokenEntry.Text = await _store.GetBearerTokenAsync();
    }

    private void OnHapticsToggled(object? sender, ToggledEventArgs e)
    {
        _haptics.Enabled = e.Value;
        _haptics.Tap();
    }

    private void SaveConfigFields()
    {
        _config.BaseUrl = BaseUrlEntry.Text;
        _config.EntraClientId = ClientIdEntry.Text;
        _config.EntraTenantId = TenantIdEntry.Text;
        _config.EntraScope = ScopeEntry.Text;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        SaveConfigFields();
        await _store.SetFakeUserAsync(FakeUserEntry.Text);
        await _store.SetBearerTokenAsync(TokenEntry.Text);
        ResultLabel.Text = $"Saved settings for {_config.BaseUrl}";
    }

    private async void OnEntraSignInClicked(object? sender, EventArgs e)
    {
        try
        {
            SaveConfigFields();
            await _entraAuth.SignInWithPkceAsync();
            TokenEntry.Text = await _store.GetBearerTokenAsync();
            ResultLabel.Text = "Signed in with Microsoft Entra ID (token stored in SecureStorage).";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Sign-in error: {ex.Message}";
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        await _entraAuth.SignOutAsync();
        TokenEntry.Text = string.Empty;
        ResultLabel.Text = "Cleared Bearer token from SecureStorage.";
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        try
        {
            var me = await _client.GetAuthMeAsync();
            ResultLabel.Text = me is { IsAuthenticated: true } ? $"Authenticated: {me.Name ?? me.UserId} ({me.AuthScheme})" : "Connected, anonymous.";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Error: {ex.Message}";
        }
    }
}
