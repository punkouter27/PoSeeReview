using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Shared Playwright browser for the E2E UI suite (NET_RULES 2.2: E2EUI = C# Playwright).
/// Tracks console errors, unhandled page exceptions, failed requests, HTTP >= 400 responses,
/// and #blazor-error-ui visibility across every page opened in the suite.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "https://localhost:5001";

    private IPlaywright _playwright = null!;
    private readonly List<IPage> _trackedPages = [];
    private readonly ConcurrentQueue<string> _browserErrors = new();
    private readonly ConcurrentDictionary<IPage, List<string>> _allowed404Paths = new();

    public void AllowNotFoundPath(IPage page, string pathFragment)
    {
        var list = _allowed404Paths.GetOrAdd(page, _ => []);
        lock (list)
        {
            list.Add(pathFragment);
        }
    }

    private bool IsAllowed404(IPage page, string method, string url)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!_allowed404Paths.TryGetValue(page, out var list))
            return false;
        lock (list)
        {
            return list.Any(fragment => url.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static TheoryData<string> Viewports => new() { "mobile", "desktop" };

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var headed = string.Equals(Environment.GetEnvironmentVariable("HEADED"), "1", StringComparison.OrdinalIgnoreCase);
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = !headed });
    }

    public async Task<IPage> NewPageAsync(string viewport = "desktop")
    {
        var options = new BrowserNewContextOptions { IgnoreHTTPSErrors = true };

        if (string.Equals(viewport, "mobile", StringComparison.OrdinalIgnoreCase))
        {
            options.ViewportSize = new ViewportSize { Width = 390, Height = 844 };
            options.IsMobile = true;
            options.HasTouch = true;
        }
        else
        {
            options.ViewportSize = new ViewportSize { Width = 1366, Height = 768 };
        }

        var context = await Browser.NewContextAsync(options);
        var page = await context.NewPageAsync();
        _trackedPages.Add(page);

        page.Console += (_, m) =>
        {
            if (m.Type != "error")
                return;

            if (m.Text.Contains("status of 404 ()", StringComparison.Ordinal) && _allowed404Paths.ContainsKey(page))
                return;

            _browserErrors.Enqueue($"Console error on {page.Url}: {m.Text}");
        };
        page.PageError += (_, err) => _browserErrors.Enqueue($"PageError on {page.Url}: {err}");
        page.Crash += (_, _) => _browserErrors.Enqueue($"Page crashed on {page.Url}");
        page.RequestFailed += (_, req) =>
        {
            if (req.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
                _browserErrors.Enqueue($"RequestFailed {req.Method} {req.Url}: {req.Failure}");
        };
        page.Response += (_, res) =>
        {
            if (res.Status < 400 || res.Url.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                return;

            if (res.Status == 404 && IsAllowed404(page, res.Request.Method, res.Url))
                return;

            _browserErrors.Enqueue($"HTTP {res.Status} for {res.Request.Method} {res.Url}");
        };

        return page;
    }

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var page in _trackedPages)
            {
                try
                {
                    if (!page.IsClosed && await page.Locator("#blazor-error-ui").IsVisibleAsync())
                        _browserErrors.Enqueue($"{page.Url}: #blazor-error-ui became visible.");
                }
                catch (PlaywrightException)
                {
                    // Page already navigated away or closed.
                }
            }
        }
        finally
        {
            if (Browser is not null)
                await Browser.DisposeAsync();
            _playwright?.Dispose();
        }

        if (!_browserErrors.IsEmpty)
            throw new InvalidOperationException("E2EUI detected browser console/network errors:\n - " + string.Join("\n - ", _browserErrors));
    }
}

[CollectionDefinition("e2e-ui")]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>;
