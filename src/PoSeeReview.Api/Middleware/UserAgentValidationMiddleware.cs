using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace PoSeeReview.Api.Middleware;

/// <summary>
/// Rejects requests that are likely made by scraping bots or lack a valid browser user agent.
/// Helps protect downstream APIs and rate limits from abuse.
/// </summary>
public sealed class UserAgentValidationMiddleware
{
    private static readonly Regex AllowedBrowserPattern = new(
        pattern: "(Mozilla/5.0|Chrome/|Safari/|Edg/|Firefox/|Mobile)",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] BlockedAgents =
    {
        "curl",
        "wget",
        "python-requests",
        "httpclient",
        "postmanruntime",
        "libwww-perl"
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<UserAgentValidationMiddleware> _logger;
    private readonly bool _isDisabled;

    public UserAgentValidationMiddleware(RequestDelegate next, ILogger<UserAgentValidationMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Disable validation in test environment
        _isDisabled = Environment.GetEnvironmentVariable("DISABLE_USER_AGENT_VALIDATION") == "true";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip validation if disabled (e.g., in test environment)
        if (_isDisabled)
        {
            await _next(context);
            return;
        }

        // Skip validation for health check endpoints (used by load balancers and monitoring)
        var path = context.Request.Path.Value ?? "";
        // and for /.well-known (App Link / Universal Link verifiers are Google's and Apple's bots,
        // and their files are public by design).
        if (path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var userAgent = context.Request.Headers.UserAgent.ToString();

        // Link-preview bots do not look like browsers and would otherwise be rejected at 400,
        // which is exactly why a shared comic used to unfurl as an empty card. They are allowed
        // through for page routes only — never for /api, which is where the paid AI calls and
        // the Google Maps quota live, and where a scraper actually costs something.
        if (SocialCrawlers.IsSocialCrawler(userAgent)
            && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(userAgent) || IsBlocked(userAgent))
        {
            _logger.LogWarning("Blocked request with suspicious User-Agent: {UserAgent}", string.IsNullOrWhiteSpace(userAgent) ? "<empty>" : userAgent);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported user agent",
                Detail = "Requests must originate from a supported browser or client to protect the service from automated scraping.",
                Type = "https://poseereview.app/errors/unsupported-user-agent"
            };

            await context.Response.WriteAsJsonAsync(problem);
            return;
        }

        await _next(context);
    }

    private static bool IsBlocked(string userAgent)
    {
        if (!AllowedBrowserPattern.IsMatch(userAgent))
        {
            return true;
        }

        return BlockedAgents.Any(blocked => userAgent.Contains(blocked, StringComparison.OrdinalIgnoreCase));
    }
}
