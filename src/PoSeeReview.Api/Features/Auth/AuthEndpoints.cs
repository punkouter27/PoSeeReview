using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using PoSeeReview.Api.Identity;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Auth;

/// <summary>
/// Auth slice (NET_RULES 4.4): explicit <c>/auth/login/microsoft</c>, <c>/auth/login/fake</c>,
/// <c>/auth/logout</c> and <c>/auth/me</c> routes over the BFF cookie session.
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Auth routes must be reachable without a session (login, logout, and /me so the
        // client can discover it is unauthenticated) — opt out of the global fallback policy.
        // They also opt out of the global rate limiter: session discovery is session plumbing,
        // not a spendable resource, and throttling it strands the client with no auth state.
        var group = app.MapGroup("/auth").WithTags("Auth").AllowAnonymous().DisableRateLimiting();

        group.MapGet("/login/microsoft", (string? returnUrl, IConfiguration configuration) =>
        {
            if (string.IsNullOrEmpty(configuration["AzureAd:ClientId"]))
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Microsoft sign-in not configured",
                    detail: "AzureAd:ClientId is missing from configuration.");
            }

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = SanitizeReturnUrl(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        group.MapGet("/login/fake", async (string? returnUrl, HttpContext http, IWebHostEnvironment environment) =>
        {
            // Guest bypass exists only in Development and Test (NET_RULES 4.4).
            if (!environment.AllowsFakeAuth())
            {
                return Results.NotFound();
            }

            var guestId = $"ANON{Random.Shared.Next(0, 1_000_000):D6}";
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, guestId),
                    new Claim(ClaimTypes.Name, guestId),
                    // The Po shell header shows the signed-in email; a guest gets a local one.
                    new Claim(ClaimTypes.Email, $"{guestId.ToLowerInvariant()}@guest.localhost"),
                    new Claim(ClaimTypes.Role, "Guest")
                ],
                FakeAuthHandler.SchemeName);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(SanitizeReturnUrl(returnUrl));
        });

        group.MapGet("/logout", LogoutAsync);

        group.MapGet("/me", (ClaimsPrincipal user, string? returnUrl) =>
        {
            var identity = user.Identity;
            return Results.Ok(new AuthStateDto
            {
                IsAuthenticated = identity?.IsAuthenticated == true,
                UserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                Name = identity?.Name,
                Email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("preferred_username"),
                Roles = [.. user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)],
                AuthScheme = identity?.AuthenticationType,
                ReturnUrl = SanitizeReturnUrl(returnUrl)
            });
        });

        return app;
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, string? returnUrl)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect(SanitizeReturnUrl(returnUrl ?? "/login"));
    }

    /// <summary>
    /// Open-redirect guard (NET_RULES 4.4): only local absolute paths survive;
    /// anything else collapses to "/".
    /// </summary>
    internal static string SanitizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
