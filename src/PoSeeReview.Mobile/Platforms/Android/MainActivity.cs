using Android.App;
using Android.Content;
using Android.Content.PM;
using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile;

// App Links: https://<prod host>/comic/{placeId} and /s/{code} open here when the host's
// /.well-known/assetlinks.json verifies this package (AutoVerify). Unverified, Android offers the
// browser, which shows the same comic on the web — the web fallback. poseereview://comic/{placeId}
// is the dev hook: `adb shell am start -a android.intent.action.VIEW -d poseereview://comic/<id>`.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https",
    DataHost = MobileConfig.ProductionHost,
    DataPathPrefixes = ["/comic/", "/s/"],
    AutoVerify = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = MobileConfig.AppScheme,
    DataHost = "comic")]
public class MainActivity : MauiAppCompatActivity
{
}
