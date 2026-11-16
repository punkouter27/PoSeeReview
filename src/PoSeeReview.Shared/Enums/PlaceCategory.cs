namespace PoSeeReview.Shared.Enums;

/// <summary>
/// What kind of place a comic is drawn from. Each kind has its own Google Places types, its own
/// scoring rubric and its own Hall of Fame; everything else (the pipeline, storage, moderation)
/// is shared. A row written before categories existed has none and reads as
/// <see cref="Restaurant"/>, which is what every such row was.
/// </summary>
public enum PlaceCategory
{
    Restaurant = 0,
    Apartment = 1
}

/// <summary>Wire names, parsing and copy for <see cref="PlaceCategory"/>.</summary>
public static class PlaceCategories
{
    /// <summary>The lower-case name used in query strings, DTOs and table columns.</summary>
    public static string ToWire(this PlaceCategory category) => category == PlaceCategory.Apartment ? "apartment" : "restaurant";

    /// <summary>Null or empty is <see cref="PlaceCategory.Restaurant"/>; anything unknown is not a category.</summary>
    public static bool TryParse(string? value, out PlaceCategory category)
    {
        category = PlaceCategory.Restaurant;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "restaurant" or "restaurants":
                return true;
            case "apartment" or "apartments":
                category = PlaceCategory.Apartment;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Lenient read for stored columns: anything but "apartment" is a restaurant.</summary>
    public static PlaceCategory FromWire(string? value) =>
        TryParse(value, out var category) ? category : PlaceCategory.Restaurant;

    /// <summary>"restaurant" / "apartment", for sentences.</summary>
    public static string Noun(this PlaceCategory category) => category == PlaceCategory.Apartment ? "apartment" : "restaurant";

    /// <summary>"restaurants" / "apartments", for sentences.</summary>
    public static string PluralNoun(this PlaceCategory category) => category == PlaceCategory.Apartment ? "apartments" : "restaurants";
}
