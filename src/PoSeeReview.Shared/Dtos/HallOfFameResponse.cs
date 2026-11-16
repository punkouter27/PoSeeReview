namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// The permanent weekly archive. Response of <c>GET /api/leaderboard/weekly</c>.
/// <para>
/// Comics expire after 24 hours and the live leaderboard churns with them, which is correct for
/// the product but means nothing accumulates and there is no reason to come back on Tuesday.
/// Entries here are promoted before cleanup runs and outlive the comic they came from — so the
/// image URL may 404, which is why <see cref="HallOfFameEntryDto.ImageExpired"/> exists.
/// </para>
/// </summary>
public sealed class HallOfFameResponse
{
    /// <summary>The archive shown: "restaurant" or "apartment".</summary>
    public string Category { get; set; } = "restaurant";

    /// <summary>Region the archive was drawn from.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Weeks returned, most recent first.</summary>
    public List<HallOfFameWeekDto> Weeks { get; set; } = new();
}

/// <summary>One archived week of top comics.</summary>
public sealed class HallOfFameWeekDto
{
    /// <summary>ISO week key in <c>{year}-W{week}</c> form, e.g. <c>2026-W36</c>.</summary>
    public string WeekKey { get; set; } = string.Empty;

    /// <summary>UTC date of the Monday that starts this week.</summary>
    public DateTimeOffset WeekStart { get; set; }

    /// <summary>Entries for the week, highest score first.</summary>
    public List<HallOfFameEntryDto> Entries { get; set; } = new();
}

/// <summary>A single archived comic.</summary>
public sealed class HallOfFameEntryDto
{
    public int Rank { get; set; }
    public string PlaceId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public double StrangenessScore { get; set; }

    /// <summary>Comic image URL as it stood when the entry was archived.</summary>
    public string ComicBlobUrl { get; set; } = string.Empty;

    /// <summary>When this comic was first archived.</summary>
    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>
    /// True once the underlying blob is gone. The row is kept regardless — the record that a
    /// place scored 97 is the point of an archive, and dropping it because a decorative image
    /// aged out would empty the feature every eight days.
    /// </summary>
    public bool ImageExpired { get; set; }
}
