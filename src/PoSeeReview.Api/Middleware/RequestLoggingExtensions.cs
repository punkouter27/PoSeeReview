using System.Diagnostics;
using PoSeeReview.Api.Identity;
using Serilog.Events;
using Serilog;

namespace PoSeeReview.Api.Middleware;

internal static class RequestLoggingExtensions
{
    internal static WebApplication UseCorrelationEnrichment(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var requestIdentityAccessor = ctx.RequestServices.GetRequiredService<ICurrentRequestIdentityAccessor>();
            var correlationId = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                ?? Activity.Current?.TraceId.ToString()
                ?? ctx.TraceIdentifier;
            var currentUserId = requestIdentityAccessor.GetCurrentUserId();

            using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
            using (Serilog.Context.LogContext.PushProperty("UserId", currentUserId))
            using (Serilog.Context.LogContext.PushProperty("SessionId", ctx.Request.Headers["X-Session-ID"].FirstOrDefault() ?? correlationId))
            using (Serilog.Context.LogContext.PushProperty("Environment", app.Environment.EnvironmentName))
            {
                ctx.Response.Headers["X-Correlation-ID"] = correlationId;
                await next();
            }
        });

        return app;
    }

    internal static WebApplication UseConfiguredSerilogRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (httpContext, elapsed, ex) =>
            {
                var path = httpContext.Request.Path.Value ?? string.Empty;

                // Silence browser-generated noise regardless of status code.
                if (path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase))
                {
                    return LogEventLevel.Debug;
                }

                if (ex == null && httpContext.Response.StatusCode < 400)
                {
                    if (path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".wasm.gz", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".dat.gz", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".js.map", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".js.map.gz", StringComparison.OrdinalIgnoreCase))
                    {
                        return LogEventLevel.Debug;
                    }
                }

                if (ex != null || httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode >= 400)
                {
                    return LogEventLevel.Warning;
                }

                return LogEventLevel.Information;
            };

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? "Unknown");
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.FirstOrDefault() ?? "Unknown");
            };
        });

        return app;
    }
}
