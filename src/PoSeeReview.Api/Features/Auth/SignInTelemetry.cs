#nullable enable
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PoSeeReview.Api.Features.Auth;

/// <summary>
/// Canonical cross-app sign-in telemetry. Every Po* app emits the SAME structured event so a
/// single Application Insights query answers "who has logged into my apps, and when".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ILogger and not TelemetryClient.TrackEvent.</b> Most Po* apps export telemetry through
/// OpenTelemetry (<c>UseAzureMonitor</c>), where <c>customEvents</c> is not part of the pipeline —
/// classic <c>TrackEvent</c> would need the legacy SDK alongside OTel. An <c>ILogger</c> record
/// flows through both stacks unchanged and lands in the <b>traces</b> table, with every named
/// placeholder below surfacing as a <c>customDimensions</c> key. That keeps this file free of any
/// NuGet dependency: it compiles against the shared framework alone.
/// </para>
/// <para>
/// <b>Sampling.</b> Adaptive sampling may silently drop these records, which is the worst possible
/// failure for a "who signed in" ledger — an absent user is indistinguishable from a user who never
/// came back. Each app's telemetry registration keeps sign-in records out of the sampled set.
/// </para>
/// <para>
/// <b>Dedupe.</b> Interactive OIDC fires once per sign-in, so those callers pass a null cache. Bearer
/// flows (MSAL SPA to API) validate a token on EVERY request, so those callers pass an
/// <see cref="IMemoryCache"/> and one record is kept per user per <see cref="DedupeWindow"/> —
/// turning per-request noise back into something that reads as a session.
/// </para>
/// </remarks>
public static class SignInTelemetry
{
    /// <summary>Event discriminator. The cross-app query filters on this exact value.</summary>
    public const string EventName = "UserSignedIn";

    /// <summary>Stable id so the record can be filtered by <c>EventId</c> as well as by name.</summary>
    public static readonly EventId SignInEventId = new(6001, EventName);

    /// <summary>
    /// One record per user per window for repeating (bearer) callers. Thirty minutes is short enough
    /// that a genuine return visit registers, long enough that an active SPA session does not emit
    /// hundreds of duplicates.
    /// </summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Emits the canonical sign-in record for <paramref name="principal"/>.
    /// Never throws: telemetry must not be able to fail a sign-in.
    /// </summary>
    /// <param name="logger">Destination logger; its provider carries the record to App Insights.</param>
    /// <param name="principal">The just-authenticated principal. Ignored when unauthenticated.</param>
    /// <param name="appName">Which app signed the user in — the pivot of the cross-app query.</param>
    /// <param name="provider">Identity provider that vouched for the user.</param>
    /// <param name="cache">Dedupe store for repeating callers; null for one-shot interactive sign-in.</param>
    public static void Track(
        ILogger logger,
        ClaimsPrincipal? principal,
        string appName,
        string provider = "microsoft",
        IMemoryCache? cache = null)
    {
        try
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return;
            }

            // The Entra object id is the only identifier that survives a rename or an email change,
            // so it is the join key; email and display name are carried for human readability.
            var userId = FirstClaim(
                principal,
                "oid",
                "http://schemas.microsoft.com/identity/claims/objectidentifier",
                ClaimTypes.NameIdentifier,
                "sub");

            var email = FirstClaim(
                principal,
                "email",
                "preferred_username",
                ClaimTypes.Email,
                ClaimTypes.Upn);

            var displayName = FirstClaim(principal, "name", ClaimTypes.Name, "given_name");

            var tenantId = FirstClaim(
                principal,
                "tid",
                "http://schemas.microsoft.com/identity/claims/tenantid");

            // Fall back to email so a provider that omits oid still produces a usable join key
            // rather than an "unknown" bucket that silently merges every such user into one row.
            var subject = Coalesce(userId, email, "unknown");

            if (cache is not null && !TryClaimWindow(cache, appName, subject))
            {
                return;
            }

            // The Azure Monitor OpenTelemetry exporter maps the enduser.id attribute onto the
            // user_AuthenticatedId column, which makes EVERY request, dependency and exception on
            // this activity attributable to the person — not just this one record.
            Activity.Current?.SetTag("enduser.id", subject);

            logger.LogInformation(
                SignInEventId,
                "{EventName} app={AppName} user={UserId} email={Email} name={DisplayName} tenant={TenantId} provider={Provider}",
                EventName,
                appName,
                subject,
                Coalesce(email, "(none)"),
                Coalesce(displayName, "(none)"),
                Coalesce(tenantId, "(none)"),
                provider);
        }
        catch (Exception ex)
        {
            // A telemetry defect must never cost the user their sign-in.
            logger.LogDebug(ex, "Sign-in telemetry could not be recorded.");
        }
    }

    /// <summary>
    /// Resolves the logger — and, when <paramref name="dedupe"/> is set, the dedupe cache — from the
    /// request's service provider, then emits the record. Keeps authentication event handlers to a
    /// single line.
    /// </summary>
    /// <param name="http">Request whose service provider supplies the logger and dedupe cache.</param>
    /// <param name="principal">The just-authenticated principal. Ignored when unauthenticated.</param>
    /// <param name="appName">Which app signed the user in — the pivot of the cross-app query.</param>
    /// <param name="provider">Identity provider that vouched for the user.</param>
    /// <param name="dedupe">
    /// True for bearer schemes, whose token is validated on every request; false for interactive
    /// OIDC, which already fires exactly once per sign-in.
    /// </param>
    public static void TrackFrom(
        HttpContext http,
        ClaimsPrincipal? principal,
        string appName,
        string provider = "microsoft",
        bool dedupe = false)
    {
        var logger = http.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger(typeof(SignInTelemetry).FullName!)
            : (ILogger)NullLogger.Instance;

        // Absent registration must not silently disable dedupe for a bearer caller: without a cache
        // every request would emit a record, so fall back to a private cache rather than to null.
        var cache = dedupe
            ? http.RequestServices.GetService(typeof(IMemoryCache)) as IMemoryCache ?? FallbackCache
            : null;

        Track(logger, principal, appName, provider, cache);
    }

    /// <summary>Used only when the app never called <c>AddMemoryCache()</c>.</summary>
    private static readonly IMemoryCache FallbackCache = new MemoryCache(new MemoryCacheOptions());

    /// <summary>
    /// Returns true when this call owns the current window for the user, i.e. no record has been
    /// emitted for them within <see cref="DedupeWindow"/>.
    /// </summary>
    private static bool TryClaimWindow(IMemoryCache cache, string appName, string subject)
    {
        var key = $"{EventName}:{appName}:{subject}";
        if (cache.TryGetValue(key, out _))
        {
            return false;
        }

        cache.Set(key, true, DedupeWindow);
        return true;
    }

    /// <summary>First non-empty value among <paramref name="claimTypes"/>, in preference order.</summary>
    private static string? FirstClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>First non-empty candidate; the last one is expected to be a literal fallback.</summary>
    private static string Coalesce(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return "unknown";
    }
}
