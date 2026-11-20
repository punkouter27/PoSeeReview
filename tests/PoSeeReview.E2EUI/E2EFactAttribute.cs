using System.Runtime.InteropServices;
using Xunit;

namespace PoSeeReview.E2EUI;

/// <summary>
/// [Fact] for the E2E UI tier, which drives a real browser against a running app: skips with one
/// clear reason when no app answers at <c>E2E_BASE_URL</c> or Playwright's Chromium is not
/// installed, instead of failing every test. This tier is not part of CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (E2EAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>[Theory] counterpart of <see cref="E2EFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (E2EAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Checks once per test run that an app answers and a browser is installed.</summary>
public static class E2EAvailability
{
    /// <summary>The app under test: <c>E2E_BASE_URL</c>, else the local default.</summary>
    public static string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("E2E_BASE_URL") is { Length: > 0 } url ? url : "https://localhost:5001").TrimEnd('/');

    private static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when the tier can run; otherwise why it is skipped.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Probe()
    {
        if (!ChromiumInstalled())
        {
            return "Playwright Chromium is not installed, so the E2E UI tier is skipped. " +
                   "Build, then run: pwsh tests/PoSeeReview.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium";
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                // Local dev certificates are self-signed.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = client.GetAsync(new Uri(BaseUrl + "/health/live")).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return "No app answers at " + BaseUrl + ", so the E2E UI tier is skipped. " +
                   "Start the app locally (see AGENTS.md) or set E2E_BASE_URL to a running copy.";
        }
    }

    private static bool ChromiumInstalled()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(root) || root == "0")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(home, "Library", "Caches", "ms-playwright")
                    : Path.Combine(home, ".cache", "ms-playwright");
        }

        return Directory.Exists(root)
            && Directory.EnumerateDirectories(root, "chromium*").Any();
    }
}
