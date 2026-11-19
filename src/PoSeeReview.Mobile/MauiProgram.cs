using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using PoSeeReview.Mobile.Pages;
using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiMaps()
            .ConfigureLifecycleEvents(ForwardAppLinks)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton<SecureSessionStore>();
        builder.Services.AddSingleton<EntraAuthService>();
        builder.Services.AddSingleton<OfflineComicCache>();
        builder.Services.AddSingleton<LocationService>();
        builder.Services.AddSingleton<Haptics>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            // UserAgentValidationMiddleware requires a valid User-Agent header.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoSeeReview.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<PoSeeReviewMobileClient>();

        builder.Services.AddTransient<NearbyPage>();
        builder.Services.AddTransient<BoardPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<ComicPage>();

        return builder.Build();
    }

    // App Links (Android) and Universal Links / the custom scheme (iOS) arrive through the platform
    // lifecycle; both end in App.OnAppLinkRequestReceived.
    private static void ForwardAppLinks(ILifecycleBuilder events)
    {
#if ANDROID
        events.AddAndroid(android => android
            .OnCreate((activity, _) => Forward(activity.Intent))
            .OnNewIntent((_, intent) => Forward(intent)));

        static void Forward(Android.Content.Intent? intent)
        {
            if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is { } data && Uri.TryCreate(data, UriKind.Absolute, out var uri))
            {
                Application.Current?.SendOnAppLinkRequestReceived(uri);
            }
        }
#elif IOS
        events.AddiOS(ios => ios
            .SceneWillConnect((_, _, options) =>
            {
                foreach (var activity in options.UserActivities)
                {
                    Forward((activity as Foundation.NSUserActivity)?.WebPageUrl);
                }

                foreach (var context in options.UrlContexts)
                {
                    Forward((context as UIKit.UIOpenUrlContext)?.Url);
                }
            })
            .SceneOpenUrl((_, contexts) =>
            {
                foreach (var context in contexts)
                {
                    Forward(context.Url);
                }
            })
            .SceneContinueUserActivity((_, activity) =>
            {
                Forward(activity.WebPageUrl);
                return true;
            }));

        static void Forward(Foundation.NSUrl? url)
        {
            if (url?.AbsoluteString is { } s && Uri.TryCreate(s, UriKind.Absolute, out var uri))
            {
                Application.Current?.SendOnAppLinkRequestReceived(uri);
            }
        }
#endif
    }
}
