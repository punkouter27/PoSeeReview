
// Only configure Serilog if not running in test mode
using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoSeeReview.Api.Features.Auth;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features;
using PoSeeReview.Api.Health;
using PoSeeReview.Api.HostedServices;
using PoSeeReview.Api.Identity;
using PoSeeReview.Api.Middleware;
using PoSeeReview.Api.Telemetry;
using PoSeeReview.Api.Configuration;
using Scalar.AspNetCore;
using Serilog.Events;
using PoSeeReview.Api.Platform;
using Serilog;

var isTestMode = Environment.GetEnvironmentVariable("DISABLE_SERILOG") == "true";

if (!isTestMode)
{
    // Configure Serilog early for startup logging
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .CreateBootstrapLogger();
}

try
{
    if (!isTestMode)
    {
        Log.Information("Starting SeeReview API");
    }

    var builder = WebApplication.CreateBuilder(args);

    builder.ConfigureAzureKeyVault(isTestMode);

    // Replace default logging with Serilog (unless in test mode)
    if (!isTestMode)
    {
        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty(PoPlatform.ApplicationProperty, PoPlatform.AppName);

            // Route structured logs to Application Insights alongside Console (NET_RULES 6.1).
            if (!string.IsNullOrEmpty(PoPlatform.ResolveAppInsightsConnectionString(context.Configuration)))
            {
                configuration.WriteTo.ApplicationInsights(
                    services.GetRequiredService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>(),
                    TelemetryConverter.Traces);
            }
        });
    }

    // Trust Azure App Service's front-end proxy so RemoteIpAddress (rate-limiter partitioning,
    // request logging) reflects the real client IP from X-Forwarded-For rather than the proxy.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Add services to the container.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddOutputCache(options =>
        options.AddPolicy(SharedReadCachePolicy.Name, new SharedReadCachePolicy()));
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddScoped<ICurrentRequestIdentityAccessor, HttpContextRequestIdentityAccessor>();
    builder.Services.AddValidatorsFromAssemblyContaining<PoSeeReview.Shared.Validation.TakedownRequestValidator>();

    builder.Services.AddConfiguredTelemetry(builder.Configuration, builder.Environment);
    builder.Services.AddConfiguredRateLimiting(builder.Configuration);

    // BFF cookie session + Entra OIDC (/common) + FakeAuth for Dev/Test (NET_RULES 4.x)
    builder.Services.AddBffAuthentication(builder.Configuration, builder.Environment);

    // Configure Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<AzureTableStorageHealthCheck>(
            "azure_table_storage",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "storage"])
        .AddCheck<AzureBlobStorageHealthCheck>(
            "azure_blob_storage",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "storage"])
        .AddCheck<GoogleMapsHealthCheck>(
            "google_maps_api",
            failureStatus: HealthStatus.Degraded,
            tags: ["ready", "external"])
        .AddCheck<SkiaImagingHealthCheck>(
            "skia_imaging",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "imaging"]);

    // Configure OpenAPI (built-in .NET 10 support)
    builder.Services.AddOpenApi();

    // CORS intentionally omitted: the API serves the Blazor WASM client from the
    // same origin (BFF), so cross-origin policy is unnecessary (NET_RULES 2.2).

    // Register infrastructure services (Azure clients)
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
    builder.Services.AddHostedService<ExpiredComicCleanupService>();
    // Fail-fast guard for required AI/Map secrets in Production (PoFunQuiz pattern).
    builder.Services.AddHostedService<StartupSecretValidator>();

    var app = builder.Build();

    // Must run before anything that reads the client IP/scheme (rate limiter, logging, redirects).
    app.UseForwardedHeaders();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    // No script/style CSP: Blazor WASM needs wasm-unsafe-eval and the client has inline bits.
    app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        return next(context);
    });

    // Add custom middleware
    // Removed RequestLoggingMiddleware - using Serilog's UseSerilogRequestLogging instead
    app.UseExceptionHandler();
    app.UseMiddleware<UserAgentValidationMiddleware>();

    app.UseCorrelationEnrichment();

    // Request logging with Serilog (disabled in test mode)
    if (!isTestMode)
    {
        app.UseConfiguredSerilogRequestLogging();
    }

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.UseWebAssemblyDebugging();
        app.MapScalarApiReference().AllowAnonymous(); // Modern API documentation UI
    }

    // HTTPS redirection — disabled for Test/E2E environments that operate on HTTP only.
    // Health endpoints are exempted so platform probes (App Service / load balancers) that
    // hit them over plain HTTP get a 200 instead of a 307 redirect that reads as "unhealthy".
    if (!app.Environment.IsEnvironment(HostEnvironments.Test))
    {
        app.UseWhen(
            ctx => !ctx.Request.Path.StartsWithSegments("/health"),
            branch => branch.UseHttpsRedirection());
    }

    // Link-preview crawlers on /comic/{placeId} get a real Open Graph document; everyone else
    // falls through to the SPA. Sits after the HTTPS branch so the canonical URL it emits is
    // https, and before UseRateLimiter/UseAuthentication because a 429 or 401 on a preview fetch
    // unfurls as the same blank card as no tags at all.
    app.UseSocialPreview();

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.UseRateLimiter();

    // After authorization on purpose: a cached body is only ever served to a caller who passed it.
    app.UseOutputCache();

    // Health check endpoints (/health, /health/live, /health/ready)
    app.MapHealthEndpoints();

    // Feature slices (Minimal API, MapGroup) — NET_RULES 3.3
    app.MapFeatureEndpoints();

    // Unknown /api/* routes must 404 as an API rather than fall through to the SPA shell.
    // Registered as a low-priority catch-all: real feature endpoints are more specific and
    // still win; only unmatched /api paths reach this.
    app.MapFallback("/api/{**segments}", () => Results.NotFound()).AllowAnonymous();

    // SPA fallback: serve index.html for every other unmatched (non-file) route so an
    // unauthenticated user can load the Blazor client and reach the login flow. This MUST be a
    // real endpoint in the main pipeline — not a MapWhen branch after UseAuthorization — so its
    // AllowAnonymous metadata is honored and the deny-by-default fallback policy
    // (RequireAuthenticatedUser) no longer 401s "/" and client-side routes. In a published app
    // "/" is not served by static files, so without this the SPA shell never loads.
    // Rate limiting is disabled here on purpose: a 429 on the SPA document returns no HTML at
    // all, so the user gets a blank white page with no error UI and no way to retry. The
    // limiter still guards the business /api/* slices, which is where the spend actually is.
    app.MapFallbackToFile("index.html").AllowAnonymous().DisableRateLimiting();

    if (!isTestMode)
    {
        Log.Information("SeeReview API started successfully");
    }

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    if (!isTestMode)
    {
        Log.Fatal(ex, "Application terminated unexpectedly");
    }
    // Always rethrow so WebApplicationFactory can see the exception
    throw;
}
finally
{
    if (!isTestMode)
    {
        Log.CloseAndFlush();
    }
}

// Make Program class accessible to integration tests
public partial class Program { }
