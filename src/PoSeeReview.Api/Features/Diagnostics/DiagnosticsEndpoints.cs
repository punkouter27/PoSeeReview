using PoSeeReview.Api.Testing;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

/// <summary>
/// Diagnostics slice: <c>GET /api/diag</c> (dependency health, build stamp, environment) and
/// <c>GET /api/diag/mock-status</c> (the USING MOCK DATA banner). Both are anonymous in every
/// environment so the <c>/diag</c> page works signed out; the snapshot handler is what keeps the
/// output safe. Under <c>/api</c> since 2026-11-17 so a browser navigation to <c>/diag</c> reaches
/// the client page instead of this JSON.
/// </summary>
internal static class DiagnosticsEndpoints
{
    public const string RoutePrefix = "/api/diag";

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("Diagnostics").AllowAnonymous();

        group.MapGet("", async (DiagnosticsSnapshotQueryHandler handler, HttpContext http, CancellationToken cancellationToken) =>
        {
            // A cached snapshot would show a stale status on Refresh.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await handler.ExecuteAsync(cancellationToken));
        });

        group.MapGet("/mock-status", (IEnumerable<IMockable> mockables, IConfiguration configuration) =>
        {
            var activeMocks = mockables.Select(m => m.GetType().Name).Distinct().ToList();
            var isMock = activeMocks.Count > 0 || configuration.GetValue<bool>(MockDataMode.ConfigurationKey);
            return Results.Ok(new MockStatusDto { IsMockActive = isMock, ActiveMocks = activeMocks });
        });

        return app;
    }
}
