using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// A generated comic strip derived from a restaurant's reviews. Consumed by the Comics and
/// Takedowns slices, so it lives in Shared (NET_RULES 2.2).
/// </summary>
public class Comic
{
    /// <summary>
    /// Unique identifier for the comic
    /// </summary>
    public ComicId Id { get; set; } = ComicId.Empty;

    /// <summary>
    /// Google Maps place identifier of the restaurant
    /// </summary>
    public PlaceId PlaceId { get; set; } = PlaceId.Empty;

    /// <summary>
    /// Name of the restaurant
    /// </summary>
    public string RestaurantName { get; set; } = string.Empty;

    /// <summary>
    /// URL to the comic image in Azure Blob Storage
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Narrative paragraph describing the strange aspects of the restaurant
    /// </summary>
    public string Narrative { get; set; } = string.Empty;

    /// <summary>
    /// Strangeness score from 0-100 (0 = normal, 100 = extremely bizarre)
    /// </summary>
    public int StrangenessScore { get; set; }

    /// <summary>
    /// Cache expiration timestamp (24 hours from creation)
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Whether this comic was generated fresh, served from cache, or regenerated after expiry.
    /// </summary>
    public ComicCacheState CacheState { get; set; } = ComicCacheState.Generated;

    /// <summary>
    /// Timestamp when the comic was generated
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Originating principal, so dev-bypass users can still be traced in storage.
    /// </summary>
    public UserId RequestedByUserId { get; set; } = UserId.Anonymous;

    /// <summary>
    /// The <c>Comics:PromptVersion</c> in force when this comic was drawn.
    /// <para>
    /// Part of the cache key, not just a record. The cache is keyed by place and age, so without
    /// a version a prompt tune kept serving the previous output until somebody paid for a forced
    /// regeneration — which made evaluating a prompt change cost one generation per restaurant
    /// tested, and made it easy to compare a new prompt against an old comic. A row whose version
    /// differs simply misses the cache. Zero means the row predates versioning, which also misses.
    /// </para>
    /// </summary>
    public int PromptVersion { get; set; }

    /// <summary>
    /// The narrator caption drawn onto each panel, in panel order. Kept because the captions
    /// otherwise exist only as pixels, which a screen reader cannot read. Empty on rows written
    /// before they were persisted.
    /// </summary>
    public string[] Captions { get; set; } = [];

    /// <summary>
    /// What each panel shows, in panel order: the shot list the painter drew from. With the
    /// captions it is the strip's alt text; the captions alone say what is written, not what is
    /// pictured. Empty on rows written before scenes were persisted.
    /// </summary>
    public string[] Scenes { get; set; } = [];

    /// <summary>The three ratings the score was computed from. Null on older comics.</summary>
    public ComicScoreBreakdown? Breakdown { get; set; }

    /// <summary>Which reviews each panel was drawn from. Empty on older comics.</summary>
    public ComicSource[] Sources { get; set; } = [];

    /// <summary>
    /// The image model that drew the current artwork. Empty on comics drawn before it was
    /// recorded, which are never remastered.
    /// </summary>
    public string ImageModel { get; set; } = string.Empty;

    /// <summary>Whether this comic has been redrawn by the remaster model, or tried and could not be.</summary>
    public RemasterState RemasterState { get; set; } = RemasterState.None;

    /// <summary>Restaurant or apartment: which rubric scored it and which board it ranks on.</summary>
    public PlaceCategory Category { get; set; } = PlaceCategory.Restaurant;
}
