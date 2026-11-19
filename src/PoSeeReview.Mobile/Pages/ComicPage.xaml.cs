using PoSeeReview.Mobile.Services;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Mobile.Pages;

/// <summary>
/// One place's comic: shown from the server or the offline copy, drawn on request (one haptic tap
/// per pipeline step, a firmer one on the reveal), and shared as its short link so a recipient
/// without the app gets the web page.
/// Routes: <c>comic?placeId=…[&amp;name=…]</c> and <c>comic?code=…</c> (an /s/ short link).
/// </summary>
public partial class ComicPage : ContentPage, IQueryAttributable
{
    private static readonly Dictionary<ComicGenerationPhase, string> Steps = new()
    {
        [ComicGenerationPhase.CacheHit] = "Already drawn — fetching it…",
        [ComicGenerationPhase.FetchingReviews] = "1/5 · Reading the reviews…",
        [ComicGenerationPhase.AnalyzingStrangeness] = "2/5 · Scoring the strangeness…",
        [ComicGenerationPhase.GeneratingArtwork] = "3/5 · Drawing the panels…",
        [ComicGenerationPhase.ComposingStrip] = "4/5 · Lettering the captions…",
        [ComicGenerationPhase.Publishing] = "5/5 · Publishing your strip…",
    };

    private readonly PoSeeReviewMobileClient _client;
    private readonly Haptics _haptics;
    private string? _placeId;
    private string? _code;
    private string _name = "Comic";
    private bool _loaded;

    public ComicPage(PoSeeReviewMobileClient client, Haptics haptics)
    {
        _client = client;
        _haptics = haptics;
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _placeId = query.TryGetValue("placeId", out var id) ? id as string : null;
        _code = query.TryGetValue("code", out var code) ? code as string : null;
        _name = query.TryGetValue("name", out var name) && name is string n && n.Length > 0 ? n : "Comic";
        _loaded = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        TitleLabel.Text = _name;
        await BusyAsync(LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_placeId is null && _code is not null)
            {
                StatusLabel.Text = "Opening shared link…";
                _placeId = await _client.ResolveShortCodeAsync(_code);
            }

            if (_placeId is null)
            {
                StatusLabel.Text = "That link doesn't point to a comic any more.";
                return;
            }

            StatusLabel.Text = "Loading…";
            Show(await _client.GetComicAsync(_placeId), revealed: false);
        }
        catch (HttpRequestException)
        {
            StatusLabel.Text = "Couldn't reach PoSeeReview, and there's no offline copy of this comic.";
        }
    }

    private async void OnDrawClicked(object? sender, EventArgs e)
    {
        if (_placeId is null)
        {
            return;
        }

        DrawButton.IsVisible = false;
        StepLabel.IsVisible = true;
        StatusLabel.Text = "Drawing — this takes about half a minute.";
        _haptics.Tap();
        var progress = new Progress<ComicGenerationPhase>(phase =>
        {
            StepLabel.Text = Steps.GetValueOrDefault(phase, "Working…");
            _haptics.Tap();
        });

        await BusyAsync(async () =>
        {
            try
            {
                Show(await _client.DrawComicAsync(_placeId, progress), revealed: true);
            }
            catch (ComicGenerationException ex)
            {
                _haptics.Error();
                StatusLabel.Text = ex.ErrorType == "insufficient_strangeness"
                    ? "Too normal — these reviews aren't strange enough for a comic. 🎺"
                    : ex.Message;
                DrawButton.IsVisible = ex.ErrorType != "insufficient_strangeness" && ex.Status != 451;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _haptics.Error();
                StatusLabel.Text = "The connection dropped while drawing. Check your signal and try again.";
                DrawButton.IsVisible = true;
            }
            finally
            {
                StepLabel.IsVisible = false;
            }
        });
    }

    private void Show(ComicResult result, bool revealed)
    {
        DrawButton.IsVisible = result.State == ComicState.NotDrawn;
        StatusLabel.Text = result.State switch
        {
            ComicState.Ready when result.FromCache => $"Offline copy · drawn {result.Comic!.GeneratedAt.ToLocalTime():MMM d}",
            ComicState.Ready => string.Empty,
            ComicState.NotDrawn => "No comic yet. Drawing one uses today's allowance.",
            ComicState.Withheld => "This comic has been withheld.",
            ComicState.SignInNeeded => "Sign in on the Settings tab to see comics.",
            _ => "This comic isn't available offline yet — open it once with signal to keep a copy.",
        };

        if (result is not { State: ComicState.Ready, Comic: { } comic, ImagePath: { } path })
        {
            return;
        }

        TitleLabel.Text = comic.RestaurantName;
        ComicImage.Source = ImageSource.FromFile(path);
        ComicImage.IsVisible = true;
        ScoreLabel.Text = $"Strangeness {comic.StrangenessScore}/100";
        ScoreLabel.IsVisible = true;
        NarrativeLabel.Text = comic.Narrative;
        NarrativeLabel.IsVisible = comic.Narrative.Length > 0;
        ShareButton.IsVisible = !result.FromCache || Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
        _ = RevealAsync(revealed);
    }

    // A freshly drawn comic develops in with a firm buzz; an already-drawn one just appears.
    private async Task RevealAsync(bool fresh)
    {
        if (!fresh)
        {
            ComicImage.Opacity = 1;
            return;
        }

        _haptics.Confirm();
        await ComicImage.FadeToAsync(1, 900, Easing.CubicOut);
    }

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_placeId is null)
        {
            return;
        }

        _haptics.Tap();
        try
        {
            var link = await _client.CreateShareLinkAsync(_placeId);
            if (link is null)
            {
                StatusLabel.Text = "Couldn't make a share link. Try again in a moment.";
                return;
            }

            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = $"PoSeeReview: {TitleLabel.Text}",
                Text = $"{TitleLabel.Text} — {ScoreLabel.Text}. Certified strange:",
                Uri = link,
            });
        }
        catch (HttpRequestException)
        {
            StatusLabel.Text = "Sharing needs a connection.";
            _haptics.Error();
        }
    }

    private async Task BusyAsync(Func<Task> work)
    {
        Busy.IsVisible = Busy.IsRunning = true;
        try
        {
            await work();
        }
        finally
        {
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }
}
