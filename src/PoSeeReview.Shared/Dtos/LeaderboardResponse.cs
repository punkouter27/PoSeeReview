namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// Response wrapper for GET /api/leaderboard endpoint
/// Contains list of entries and metadata
/// </summary>
public class LeaderboardResponse
{
    /// <summary>The board shown: "restaurant" or "apartment".</summary>
    public string Category { get; set; } = "restaurant";

    /// <summary>
    /// ISO 3166-1 alpha-2 country code for this leaderboard
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// List of leaderboard entries, sorted by strangeness score descending
    /// </summary>
    public List<LeaderboardEntryDto> Entries { get; set; } = new();

    /// <summary>
    /// Timestamp when this response was generated
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }
}
