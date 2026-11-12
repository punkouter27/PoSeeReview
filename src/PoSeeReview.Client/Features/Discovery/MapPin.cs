namespace PoSeeReview.Client.Features.Discovery;

/// <summary>Where the user left the map after panning or zooming it.</summary>
public readonly record struct MapArea(double Lat, double Lng, double Zoom);

/// <summary>
/// One place on a <see cref="MapView"/>. Crosses into <c>map-view.js</c> as JSON written by
/// <see cref="Services.AppJsonContext"/>, so the trimmed client never serializes it reflectively.
/// </summary>
public sealed class MapPin
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }

    /// <summary>Strangeness of the live comic; null when the place has not been drawn yet.</summary>
    public int? Score { get; set; }

    /// <summary>What a screen reader hears for the pin, score included.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Google returns 0,0 for a place it could not locate; that is the Gulf of Guinea, not a restaurant.</summary>
    public static bool HasCoordinates(double? lat, double? lng) =>
        lat is { } la && lng is { } lo && (la != 0 || lo != 0) && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180;
}
