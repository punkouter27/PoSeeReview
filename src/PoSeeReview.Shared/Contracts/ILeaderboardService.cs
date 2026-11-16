using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// Manages the global strangeness leaderboard (top N comics per region). Declared in Shared
/// because the Comics slice publishes scores to it without referencing the Leaderboard slice
/// (NET_RULES 2.2).
/// </summary>
public interface ILeaderboardService
{
    /// <summary>
    /// Retrieves top N comics for a region ranked by strangeness score. An empty region means
    /// the worldwide board — the top N across every region.
    /// </summary>
    /// <param name="region">Geographic region code (e.g. US-WA-Seattle), or empty for all regions</param>
    /// <param name="limit">Number of entries to return (default 10, max 50)</param>
    /// <param name="category">The restaurant board or the apartment board</param>
    /// <returns>List of leaderboard entries with assigned ranks</returns>
    Task<List<LeaderboardEntry>> GetTopComicsAsync(RegionCode region, int limit = 10, PlaceCategory category = PlaceCategory.Restaurant);

    /// <summary>
    /// Inserts or updates a leaderboard entry, keeping the higher of the two scores.
    /// </summary>
    /// <param name="entry">Leaderboard entry to upsert</param>
    Task UpsertEntryAsync(LeaderboardEntry entry);
}
