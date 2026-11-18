using Microsoft.Extensions.Logging;
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
        builder.Services.AddSingleton<OfflineComicCache>();
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

        return builder.Build();
    }
}
