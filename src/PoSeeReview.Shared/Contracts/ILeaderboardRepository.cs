using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// Persistence for leaderboard rows in Azure Table Storage (inverted RowKey gives descending
/// sort by strangeness score). Declared in Shared because the Takedowns slice erases entries
/// without referencing the Leaderboard slice (NET_RULES 2.2).
/// </summary>
public interface ILeaderboardRepository
{
    /// <summary>
    /// Retrieves top N entries for a region, pre-sorted by strangeness score (descending).
    /// </summary>
    /// <param name="region">Geographic region code</param>
    /// <param name="limit">Maximum number of entries to return</param>
    /// <param name="category">Only this board; null reads both.</param>
    /// <returns>List of leaderboard entries sorted by score (highest first)</returns>
    Task<List<LeaderboardEntry>> GetTopEntriesAsync(RegionCode region, int limit, PlaceCategory? category = null);

    /// <summary>
    /// Retrieves the top N entries across every region. A cross-partition scan, honest at this
    /// app's volume and capped well below anything that could timeout.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return</param>
    /// <param name="category">Only this board; null reads both.</param>
    /// <returns>List of leaderboard entries sorted by score (highest first)</returns>
    Task<List<LeaderboardEntry>> GetTopEntriesGlobalAsync(int limit, PlaceCategory? category = null);

    /// <summary>
    /// Gets a specific leaderboard entry by place identifier and region.
    /// </summary>
    /// <param name="placeId">Google Maps place identifier</param>
    /// <param name="region">Geographic region code</param>
    /// <returns>Leaderboard entry if found, null otherwise</returns>
    Task<LeaderboardEntry?> GetByPlaceIdAsync(PlaceId placeId, RegionCode region);

    /// <summary>
    /// Gets a leaderboard entry by place identifier, searching across all regions.
    /// Used by the cleanup service to check whether a blob is still referenced.
    /// </summary>
    /// <param name="placeId">Google Maps place identifier</param>
    /// <returns>First matching leaderboard entry, or null if not found in any region</returns>
    Task<LeaderboardEntry?> GetByPlaceIdAsync(PlaceId placeId);

    /// <summary>
    /// Inserts or updates a leaderboard entry (upsert on PlaceId/Region).
    /// </summary>
    /// <param name="entry">Leaderboard entry to persist</param>
    Task UpsertAsync(LeaderboardEntry entry);

    /// <summary>
    /// Deletes a leaderboard entry.
    /// </summary>
    /// <param name="placeId">Google Maps place identifier</param>
    /// <param name="region">Geographic region code</param>
    Task DeleteAsync(PlaceId placeId, RegionCode region);
}
