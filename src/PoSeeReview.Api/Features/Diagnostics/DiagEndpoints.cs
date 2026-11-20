using PoSeeReview.Api.Testing;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

/// <summary>
/// Diagnostics slice: <c>GET /api/diag</c> (the /diag contract in AGENTS.md: dependency health,
/// version, environment) and <c>GET /api/diag/mock-status</c> (the USING MOCK DATA badge). Both
/// are anonymous in every environment so the <c>/diag</c> page works signed out;
/// <see cref="DiagReportHandler"/> is what keeps the output safe.
/// </summary>
internal static class DiagEndpoints
{
    public const string RoutePrefix = "/api/diag";

    public static IEndpointRouteBuilder MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(RoutePrefix).WithTags("Diagnostics").AllowAnonymous();

        group.MapGet("", async (DiagReportHandler handler, HttpContext http, CancellationToken cancellationToken) =>
        {
            // A cached report would show a stale status on Refresh.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await handler.ExecuteAsync(cancellationToken));
        }).Produces<DiagReportDto>();

        group.MapGet("/mock-status", (IEnumerable<IMockable> mockables, IConfiguration configuration) =>
        {
            var activeMocks = mockables.Select(m => m.GetType().Name).Distinct().ToList();
            var isMock = activeMocks.Count > 0 || configuration.GetValue<bool>(MockDataMode.ConfigurationKey);
            return Results.Ok(new MockStatusDto { IsMockActive = isMock, ActiveMocks = activeMocks });
        });

        return app;
    }
}
