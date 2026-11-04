namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// Data transfer object for comic strip with strangeness score
/// </summary>
public class ComicDto
{
    /// <summary>
    /// Unique identifier for the comic
    /// </summary>
    public string ComicId { get; set; } = string.Empty;

    /// <summary>
    /// Google Maps Place ID of the restaurant
    /// </summary>
    public string PlaceId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the restaurant
    /// </summary>
    public string RestaurantName { get; set; } = string.Empty;

    /// <summary>
    /// Narrative paragraph describing the strange aspects
    /// </summary>
    public string Narrative { get; set; } = string.Empty;

    /// <summary>
    /// Strangeness score from 0-100 (0 = normal, 100 = extremely bizarre)
    /// </summary>
    public int StrangenessScore { get; set; }

    /// <summary>
    /// HTTPS URL to comic PNG in Azure Blob Storage
    /// </summary>
    public string BlobUrl { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the comic was generated
    /// </summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>
    /// Cache expiration timestamp (generatedAt + 24 hours)
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// True if returned from cache, false if newly generated
    /// </summary>
    public bool IsCached { get; set; }

    /// <summary>
    /// The caption drawn onto each panel, in order. The client uses them as the image's alt
    /// text: they are baked into the pixels, so without this a screen reader gets nothing of the
    /// strip but its title. Empty on comics drawn before captions were stored.
    /// </summary>
    public string[] Captions { get; set; } = [];

    /// <summary>
    /// What each panel pictures, in order. Paired with <see cref="Captions"/> in the alt text, so
    /// a screen reader hears the picture as well as the words on it. Empty on older comics.
    /// </summary>
    public string[] Scenes { get; set; } = [];

    /// <summary>True once the popular-comic remaster has redrawn the artwork.</summary>
    public bool IsRemastered { get; set; }
}

