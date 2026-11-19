using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new AppShell());

    /// <summary>A shared comic link opened the app: go straight to that comic.</summary>
    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        if (LinkRouter.ToRoute(uri) is not { } route)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // On a cold start the link can arrive before the Shell exists.
            for (var i = 0; Shell.Current is null && i < 50; i++)
            {
                await Task.Delay(100);
            }

            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync(route);
            }
        });
    }
}
