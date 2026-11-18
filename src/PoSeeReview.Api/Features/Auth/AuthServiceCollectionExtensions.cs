using PoSeeReview.Api.Identity;
using PoSeeReview.Api.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PoSeeReview.Api.Features.Auth;

/// <summary>
/// BFF cookie-proxy authentication (NET_RULES 4.2/4.3): the WASM client never touches
/// tokens — sessions live in an HttpOnly, SameSite=Strict, encrypted cookie. Microsoft
/// Entra OIDC uses the multi-tenant <c>/common</c> authority with a shape-based issuer
/// validator against the allowed-tenant list. FakeAuth is a header-mapped scheme for
/// Dev/Test only.
/// </summary>
internal static class AuthServiceCollectionExtensions
{
    private const string PolicySchemeName = "BffAuth";

    internal static IServiceCollection AddBffAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var clientId = configuration["AzureAd:ClientId"];
        var authBuilder = services.AddAuthentication(options =>
            {
                options.DefaultScheme = PolicySchemeName;
                options.DefaultChallengeScheme = PolicySchemeName;
            })
            .AddPolicyScheme(PolicySchemeName, "Cookie, Bearer, or FakeAuth selector", options =>
            {
                // Fake headers only exist for the Dev/Test scheme; Bearer is for mobile clients; everything else is the cookie.
                options.ForwardDefaultSelector = context =>
                {
                    if (environment.AllowsFakeAuth() && context.Request.Headers.ContainsKey(FakeAuthHandler.UserHeader))
                        return FakeAuthHandler.SchemeName;
                    if (!string.IsNullOrEmpty(clientId)
                        && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return JwtBearerDefaults.AuthenticationScheme;
                    return CookieAuthenticationDefaults.AuthenticationScheme;
                };
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = PoPlatform.SessionCookieName(secure: true);
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                // API clients get status codes, not HTML login redirects.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        if (!string.IsNullOrEmpty(clientId))
        {
            var allowedTenants = configuration.GetSection("AzureAd:AllowedTenants").Get<string[]>() ?? [];
            authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = "https://login.microsoftonline.com/common/v2.0";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "name",
                    ValidateAudience = true,
                    ValidAudiences = [clientId, $"api://{clientId}"],
                    ValidateIssuer = true,
                    IssuerValidator = (issuer, _, _) => ValidateIssuerShape(issuer, allowedTenants),
                };
            });
            authBuilder.AddOpenIdConnect(options =>
            {
                // /common accepts both Entra work/school and personal Microsoft accounts,
                // matching the AzureADandPersonalMicrosoftAccount audience (NET_RULES 4.3).
                options.Authority = "https://login.microsoftonline.com/common/v2.0";
                options.ClientId = clientId;
                options.ClientSecret = configuration["AzureAd:ClientSecret"];
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.CallbackPath = "/auth/callback/microsoft";
                options.SignedOutCallbackPath = "/auth/callback/logout";
                options.SaveTokens = false; // BFF: tokens never persisted, never reach the browser
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                    ValidateIssuerShape(issuer, allowedTenants);
                // Canonical UserSignedIn record. OnTokenValidated fires exactly once per interactive
                // sign-in — after validation, before the session cookie is issued — so it needs no
                // dedupe, and a rejected token never reaches it.
                options.Events.OnTokenValidated = ctx =>
                {
                    SignInTelemetry.TrackFrom(ctx.HttpContext, ctx.Principal, "PoSeeReview");
                    return Task.CompletedTask;
                };
            });
        }

        if (environment.AllowsFakeAuth())
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, null);
        }

        // Deny by default (NET_RULES 4.1/4.5): every endpoint requires an authenticated
        // session unless it explicitly opts out with AllowAnonymous (auth, health, diag,
        // devsession, takedowns, SPA fallback). Client [Authorize] is UI-only.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        return services;
    }

    /// <summary>
    /// Shape-based issuer validation for the /common authority: the issuer must be
    /// https://login.microsoftonline.com/{tenantGuid}/v2.0 and the tenant must be on
    /// the allow list (NET_RULES 4.3).
    /// </summary>
    private static string ValidateIssuerShape(string issuer, string[] allowedTenants)
    {
        if (Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            && uri.Segments.Length >= 2)
        {
            var tenantId = uri.Segments[1].TrimEnd('/');
            if (Guid.TryParse(tenantId, out _)
                && allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
            {
                return issuer;
            }
        }

        throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' is not an allowed tenant.")
        {
            InvalidIssuer = issuer
        };
    }
}
