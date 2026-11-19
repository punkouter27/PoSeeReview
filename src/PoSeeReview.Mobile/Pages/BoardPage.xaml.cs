using PoSeeReview.Mobile.Services;

namespace PoSeeReview.Mobile.Pages;

public partial class BoardPage : ContentPage
{
    private readonly PoSeeReviewMobileClient _client;

    public BoardPage(PoSeeReviewMobileClient client)
    {
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        BoardList.Clear();
        try
        {
            var board = await _client.GetLeaderboardAsync();
            if (board is null)
            {
                StatusLabel.Text = "No cached leaderboard data available.";
                return;
            }

            StatusLabel.Text = $"Region: {board.Region} · {board.Entries.Count} entries · Network: {Connectivity.Current.NetworkAccess}";
            foreach (var entry in board.Entries)
            {
                var row = new Label { Text = $"#{entry.Rank} {entry.RestaurantName} — Strangeness {entry.StrangenessScore:F0}/100", Padding = new Thickness(0, 6) };
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await Shell.Current.GoToAsync(
                    $"comic?placeId={Uri.EscapeDataString(entry.PlaceId)}&name={Uri.EscapeDataString(entry.RestaurantName)}");
                row.GestureRecognizers.Add(tap);
                BoardList.Add(row);
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
        }
    }
}
