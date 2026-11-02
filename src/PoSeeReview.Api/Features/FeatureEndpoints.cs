using PoSeeReview.Api.Features.Sharing;
using PoSeeReview.Api.Features.Auth;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Diagnostics;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.Features.Restaurants;

namespace PoSeeReview.Api.Features;

/// <summary>
/// Central registration of every Minimal-API feature slice via MapGroup (NET_RULES 3.3).
/// </summary>
internal static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapComicsEndpoints();
        app.MapSharingEndpoints();
        app.MapRestaurantsEndpoints();
        app.MapLeaderboardEndpoints();
        app.MapModerationEndpoints();
        app.MapDiagnosticsEndpoints();
        return app;
    }
}
