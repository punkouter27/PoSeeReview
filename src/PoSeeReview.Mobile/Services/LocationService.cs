namespace PoSeeReview.Mobile.Services;

/// <summary>A position, or why there is none (shown to the user, who can then search by name).</summary>
public sealed record LocationResult(Location? Fix, string? Problem);

/// <summary>
/// Native GPS. Asks for when-in-use permission the first time, answers from the OS's last fix when
/// it is recent (instant, no radio), and otherwise takes one fresh medium-accuracy fix. Never
/// invents a position: a failure is said, not papered over with a default city.
/// </summary>
public sealed class LocationService
{
    private static readonly TimeSpan FreshEnough = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FixTimeout = TimeSpan.FromSeconds(10);
    private readonly IGeolocation _geolocation = Geolocation.Default;

    public static async Task<bool> HasPermissionAsync()
        => await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted;

    public async Task<LocationResult> GetAsync(CancellationToken ct = default)
    {
        var status = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var current = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            // iOS asks only once; after a "Don't allow" only the Settings app can change it.
            if (current == PermissionStatus.Granted || (current == PermissionStatus.Denied && DeviceInfo.Platform == DevicePlatform.iOS))
            {
                return current;
            }

            return await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        });

        if (status != PermissionStatus.Granted)
        {
            return new(null, "Location is off for PoSeeReview. Allow it in Settings, or search by place name.");
        }

        try
        {
            var last = await _geolocation.GetLastKnownLocationAsync();
            if (last is not null && DateTimeOffset.UtcNow - last.Timestamp < FreshEnough)
            {
                return new(last, null);
            }

            var fresh = await _geolocation.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, FixTimeout), ct);
            return fresh is not null || last is not null
                ? new(fresh ?? last, null)
                : new(null, "Couldn't get a GPS fix. Try again outside, or search by place name.");
        }
        catch (FeatureNotEnabledException)
        {
            return new(null, "Location services are turned off on this phone. Turn them on, or search by place name.");
        }
        catch (FeatureNotSupportedException)
        {
            return new(null, "This device has no location support. Search by place name instead.");
        }
        catch (PermissionException)
        {
            return new(null, "Location permission was withdrawn. Allow it in Settings, or search by place name.");
        }
    }
}
