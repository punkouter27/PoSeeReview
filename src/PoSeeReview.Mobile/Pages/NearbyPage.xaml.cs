using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using PoSeeReview.Mobile.Services;
using PoSeeReview.Shared.Dtos;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace PoSeeReview.Mobile.Pages;

public partial class NearbyPage : ContentPage
{
    private readonly PoSeeReviewMobileClient _client;
    private readonly LocationService _location;
    private readonly Haptics _haptics;
    private Map? _map;
    private bool _loadedSaved;

    public NearbyPage(PoSeeReviewMobileClient client, LocationService location, Haptics haptics)
    {
        _client = client;
        _location = location;
        _haptics = haptics;
        InitializeComponent();
    }

    // iOS always has Apple Maps. Android's Google Maps SDK crashes without a key, so the map exists
    // only in builds made with POSEEREVIEW_ANDROID_MAPS_KEY; otherwise the list carries the page.
    private static bool MapAvailable =>
#if IOS || (ANDROID && HAS_MAPS_KEY)
        true;
#else
        false;
#endif

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loadedSaved)
        {
            return;
        }

        // Open on the last search so the page is useful before (or without) any network.
        _loadedSaved = true;
        if (await _client.GetSavedNearbyAsync() is { } saved)
        {
            Show(saved, fromCache: true);
        }
    }

    private async void OnNearMeClicked(object? sender, EventArgs e)
    {
        _haptics.Tap();
        await RunAsync("Finding you…", async () =>
        {
            var result = await _location.GetAsync();
            if (result.Fix is not { } fix)
            {
                StatusLabel.Text = result.Problem;
                _haptics.Error();
                SearchEntry.Focus();
                return;
            }

            StatusLabel.Text = "Looking for strange reviews nearby…";
            await ShowSearchAsync(() => _client.GetNearbyAsync(fix.Latitude, fix.Longitude), new Location(fix.Latitude, fix.Longitude));
        });
    }

    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        var place = SearchEntry.Text?.Trim();
        if (string.IsNullOrEmpty(place))
        {
            return;
        }

        _haptics.Tap();
        await SearchEntry.HideSoftInputAsync(CancellationToken.None);
        SearchEntry.Unfocus();
        await RunAsync($"Searching {place}…", () => ShowSearchAsync(() => _client.SearchAsync(place), center: null));
    }

    private async Task ShowSearchAsync(Func<Task<SavedNearby?>> search, Location? center)
    {
        try
        {
            if (await search() is { } found)
            {
                Show(found, fromCache: Connectivity.Current.NetworkAccess != NetworkAccess.Internet, center);
            }
            else
            {
                StatusLabel.Text = "You're offline and nothing has been saved yet.";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            StatusLabel.Text = "Sign in on the Settings tab to search.";
            _haptics.Error();
        }
        catch (HttpRequestException)
        {
            // Signal but no server: fall back to the saved search rather than an empty page.
            if (await _client.GetSavedNearbyAsync() is { } saved)
            {
                Show(saved, fromCache: true);
            }
            else
            {
                StatusLabel.Text = "Couldn't reach PoSeeReview. Check your connection and try again.";
            }

            _haptics.Error();
        }
    }

    private void Show(SavedNearby result, bool fromCache, Location? center = null)
    {
        var places = result.Response.Restaurants;
        StatusLabel.Text = fromCache
            ? $"Offline — showing \"{result.Label}\" saved {result.SavedAt.ToLocalTime():MMM d, h:mm tt}."
            : result.Response.Stale ? $"Results may be out of date: {result.Response.StaleReason}"
            : places.Count == 0 ? "No places found here." : $"{places.Count} places · {result.Scores.Count} already drawn";

        PlacesList.Clear();
        foreach (var place in places)
        {
            PlacesList.Add(Card(place, Badge(place.PlaceId, result)));
        }

        ShowPins(places, result, center);
    }

    private static string Badge(string placeId, SavedNearby result)
        => result.Scores.TryGetValue(placeId, out var score) ? $"Strangeness {score}"
            : !result.Forecasts.TryGetValue(placeId, out var forecast) ? "Not drawn yet"
            : forecast < result.ForecastFloor ? "Too normal to draw"
            : $"Forecast ~{forecast}";

    private View Card(RestaurantDto place, string badge)
    {
        var card = new Border { Padding = 12, StrokeThickness = 1 };
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Add(new Label { Text = place.Name, FontAttributes = FontAttributes.Bold });
        stack.Add(new Label { Text = badge, FontSize = 13 });
        stack.Add(new Label { Text = place.Distance > 0 ? $"{place.Distance:F1} km · {place.Address}" : place.Address, FontSize = 12, Opacity = 0.75 });
        card.Content = stack;
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenAsync(place);
        card.GestureRecognizers.Add(tap);
        SemanticProperties.SetDescription(card, $"{place.Name}, {badge}");
        return card;
    }

    private void ShowPins(List<RestaurantDto> places, SavedNearby result, Location? center)
    {
        if (!MapAvailable || places.Count == 0)
        {
            MapHost.IsVisible = false;
            return;
        }

        if (_map is null)
        {
            _map = new Map { IsShowingUser = false };
            MapHost.Content = _map;
        }

        _map.Pins.Clear();
        foreach (var place in places)
        {
            var pin = new Pin
            {
                Label = place.Name,
                Address = Badge(place.PlaceId, result),
                Location = new Location(place.Latitude, place.Longitude),
            };
            pin.MarkerClicked += (_, _) => _haptics.Tap();
            pin.InfoWindowClicked += async (_, _) => await OpenAsync(place);
            _map.Pins.Add(pin);
        }

        _ = ShowUserDotAsync(_map);
        var focus = center ?? new Location(places.Average(p => p.Latitude), places.Average(p => p.Longitude));
        var radiusKm = Math.Max(0.5, places.Max(p => Location.CalculateDistance(focus, p.Latitude, p.Longitude, DistanceUnits.Kilometers)) * 1.2);
        _map.MoveToRegion(MapSpan.FromCenterAndRadius(focus, Distance.FromKilometers(radiusKm)));
        MapHost.IsVisible = true;
    }

    // The blue dot needs the permission first, or the map SDK throws.
    private static async Task ShowUserDotAsync(Map map) => map.IsShowingUser = await LocationService.HasPermissionAsync();

    private async Task OpenAsync(RestaurantDto place)
    {
        _haptics.Tap();
        await Shell.Current.GoToAsync($"comic?placeId={Uri.EscapeDataString(place.PlaceId)}&name={Uri.EscapeDataString(place.Name)}");
    }

    private async Task RunAsync(string status, Func<Task> work)
    {
        NearMeButton.IsEnabled = false;
        Busy.IsVisible = Busy.IsRunning = true;
        StatusLabel.Text = status;
        try
        {
            await work();
        }
        finally
        {
            Busy.IsVisible = Busy.IsRunning = false;
            NearMeButton.IsEnabled = true;
        }
    }
}
