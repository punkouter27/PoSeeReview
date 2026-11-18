using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile.Pages;

public partial class NearbyPage : ContentPage
{
    private readonly PoSeeReviewMobileClient _client;

    public NearbyPage(PoSeeReviewMobileClient client)
    {
        _client = client;
        InitializeComponent();
    }

    private async void OnNearMeClicked(object? sender, EventArgs e)
    {
        RestaurantsList.Clear();
        ComicPreview.IsVisible = false;
        try
        {
            var loc = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)));
            var lat = loc?.Latitude ?? 47.6062;
            var lon = loc?.Longitude ?? -122.3321;
            StatusLabel.Text = $"Searching near {lat:F4}, {lon:F4}...";

            var response = await _client.GetNearbyAsync(lat, lon);
            if (response is null)
            {
                StatusLabel.Text = "No response from API.";
                return;
            }

            if (HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.Click);
            }

            StatusLabel.Text = response.Stale
                ? $"Stale mode: {response.StaleReason ?? "check API key"}"
                : $"Found {response.Restaurants.Count} places nearby";

            foreach (var r in response.Restaurants)
            {
                var card = new Border { Padding = 12, StrokeThickness = 1 };
                var stack = new VerticalStackLayout { Spacing = 6 };
                stack.Add(new Label { Text = r.Name, FontAttributes = FontAttributes.Bold });
                stack.Add(new Label { Text = $"{r.Address} · {r.AverageRating:F1}★ ({r.TotalReviews} reviews)" });
                var shareBtn = new Button { Text = "Load & Share Comic" };
                shareBtn.Clicked += async (_, _) => await OpenAndShareComicAsync(r.PlaceId, r.Name);
                stack.Add(shareBtn);
                card.Content = stack;
                RestaurantsList.Add(card);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task OpenAndShareComicAsync(string placeId, string name)
    {
        try
        {
            var filePath = await _client.DownloadComicFileAsync(placeId);
            if (filePath is null)
            {
                StatusLabel.Text = $"No cached comic yet for {name}.";
                return;
            }

            if (HapticFeedback.Default.IsSupported)
            {
                HapticFeedback.Default.Perform(HapticFeedbackType.LongPress);
            }

            ComicPreview.Source = ImageSource.FromFile(filePath);
            ComicPreview.IsVisible = true;
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"PoSeeReview: {name}",
                File = new ShareFile(filePath),
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Share failed: {ex.Message}";
        }
    }
}
