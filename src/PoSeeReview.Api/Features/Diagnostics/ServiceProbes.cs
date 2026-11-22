using System.Diagnostics;
using System.Net;
using System.Text;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoSeeReview.Api.Configuration;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.Platform;
using PoSeeReview.Api.Testing;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

/// <summary>
/// The live service rows of <c>/api/diag</c>: every external service the app really calls is
/// reached over the network here, never just looked up in configuration. Each probe is a call
/// that costs nothing — a listing, a metadata read, or a deliberately incomplete request that the
/// service rejects only after it has accepted the key — so an anonymous visitor pressing Refresh
/// can never run up a bill. Results are cached for <see cref="CacheFor"/> for the same reason.
/// <para>
/// A service switched off on purpose (no topic configured, remastering disabled, mock mode,
/// a local run without Key Vault) is reported as <see cref="Off"/>: neutral, and left out of the
/// overall status. Only a service that is meant to be on and does not answer is Degraded or
/// Unhealthy.
/// </para>
/// </summary>
public sealed class ServiceProbes(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    IServiceProvider services,
    ILogger<ServiceProbes> logger) : IDisposable
{
    /// <summary>Status of a service that is switched off on purpose. Never counts toward the banner.</summary>
    public const string Off = "Off";

    /// <summary>The named HttpClient every probe uses (short timeout, no retries).</summary>
    public const string HttpClientName = "diag-probes";

    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private const string AzureOpenAiApiVersion = "2024-10-21";
    private const string ContentSafetyApiVersion = "2024-09-01";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, DiagCheckDto[] Rows)? _cache;

    public void Dispose() => _gate.Dispose();

    public async Task<DiagCheckDto[]> RunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } cached && timeProvider.GetUtcNow() - cached.At < CacheFor)
            {
                return cached.Rows;
            }

            var rows = await Task.WhenAll(
                Run("google-maps", "Google Maps", "Finds places and reads their public reviews.", GoogleMapsAsync, cancellationToken),
                Run("ai", "AI scoring and drawing", "Rates how strange the reviews are, writes the panels, draws the comic and voices the audio.", AiAsync, cancellationToken),
                Run("content-safety", "Content safety", "Screens generated comics before they are shown.", ContentSafetyAsync, cancellationToken),
                Run("remaster", "Gemini remaster", "Redraws top comics at higher quality.", RemasterAsync, cancellationToken),
                Run("key-vault", "Key Vault", "Supplies the app's secrets at startup.", KeyVaultAsync, cancellationToken),
                Run("microsoft-sign-in", "Microsoft sign-in", "Lets people sign in with a Microsoft account.", MicrosoftSignInAsync, cancellationToken),
                Run("owner-alerts", "Owner alerts", "Pushes moderation alerts to the owner's phone (ntfy).", OwnerAlertsAsync, cancellationToken),
                Run("telemetry", "Telemetry", "Sends logs and traces to Application Insights.", TelemetryAsync, cancellationToken));

            _cache = (timeProvider.GetUtcNow(), rows);
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A probe's verdict. Detail and Problem are fixed text: never a host, key or exception message.</summary>
    internal readonly record struct Outcome(string Status, string Detail, string? Problem = null)
    {
        public static Outcome Healthy(string detail = "Checked with a live call") => new(nameof(HealthStatus.Healthy), detail);
        public static Outcome Degraded(string problem) => new(nameof(HealthStatus.Degraded), "Answered with a problem", problem);
        public static Outcome Unhealthy(string problem) => new(nameof(HealthStatus.Unhealthy), "Not working", problem);
        public static Outcome SwitchedOff(string detail) => new(Off, detail);
    }

    private delegate Task<Outcome> Probe(HttpClient http, CancellationToken cancellationToken);

    private async Task<DiagCheckDto> Run(string name, string label, string purpose, Probe probe, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Outcome outcome;
        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            outcome = await probe(http, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "/api/diag probe {Probe} failed", name);
            outcome = Outcome.Unhealthy($"Not reachable ({ex.GetType().Name}).");
        }

        // An Off row made no call, so it has no latency to report.
        var latency = outcome.Status == Off ? 0 : Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        return new DiagCheckDto(name, DiagGroups.Service, label, outcome.Status, purpose, outcome.Detail, latency, outcome.Problem);
    }

    private bool IsMock => MockDataMode.IsEnabled(configuration, environment);

    private string? Setting(string key) => configuration[key] is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // ── Google Maps: a Places text search with no query. Google checks the key first, so a
    //    valid key gets 400 INVALID_ARGUMENT (an error, not billed) and a bad one API_KEY_INVALID.
    private async Task<Outcome> GoogleMapsAsync(HttpClient http, CancellationToken ct)
    {
        if (IsMock)
        {
            return Outcome.SwitchedOff("Mocked (sample places)");
        }

        if (Setting("GoogleMaps:ApiKey") is not { } key)
        {
            return Outcome.Degraded("The Google Maps key is not configured, so nearby places cannot be found.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Goog-Api-Key", key);
        request.Headers.Add("X-Goog-FieldMask", "places.id");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (body.Contains("API_KEY_INVALID", StringComparison.Ordinal) || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return Outcome.Unhealthy("Google rejected the Maps key, so nearby places cannot be found.");
        }

        return response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK
            ? Outcome.Healthy("Key accepted (live call)")
            : Outcome.Degraded($"Google Maps answered {(int)response.StatusCode}.");
    }

    // ── AI: lists the models on the AI resource (free), which proves the endpoint and the key.
    private async Task<Outcome> AiAsync(HttpClient http, CancellationToken ct)
    {
        if (IsMock)
        {
            return Outcome.SwitchedOff("Mocked (no real AI calls)");
        }

        if (string.Equals(Setting(InfrastructureServiceCollectionExtensions.AiChatProviderConfigurationKey), "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = Setting("Ollama:BaseUrl") ?? "http://localhost:11434/v1";
            using var local = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models", ct);
            return local.IsSuccessStatusCode
                ? Outcome.Healthy("Local model (Ollama) answered")
                : Outcome.Unhealthy($"The local model server answered {(int)local.StatusCode}.");
        }

        if (Setting("AzureOpenAI:Endpoint") is not { } endpoint || Setting("AzureOpenAI:ApiKey") is not { } key)
        {
            return Outcome.Unhealthy("The AI service is not configured, so no comic can be made.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/openai/models?api-version={AzureOpenAiApiVersion}");
        request.Headers.Add("api-key", key);
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => Outcome.Healthy(),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Outcome.Unhealthy("The AI service rejected the key, so no comic can be made."),
            _ => Outcome.Degraded($"The AI service answered {(int)response.StatusCode}.")
        };
    }

    // ── Content safety: lists the text blocklists (free). Generation falls back to a word list
    //    when the service is down, so a failure here is Degraded, not Unhealthy.
    private async Task<Outcome> ContentSafetyAsync(HttpClient http, CancellationToken ct)
    {
        if (IsMock)
        {
            return Outcome.SwitchedOff("Mocked (word list only)");
        }

        if (Setting($"{ModerationOptions.SectionName}:{nameof(ModerationOptions.ContentSafetyEndpoint)}") is not { } endpoint)
        {
            return Outcome.SwitchedOff("Off (word list only)");
        }

        if (Setting("AzureOpenAI:ApiKey") is not { } key)
        {
            return Outcome.Degraded("No key for the content safety service; the word list screens comics instead.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/contentsafety/text/blocklists?api-version={ContentSafetyApiVersion}");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? Outcome.Healthy()
            : Outcome.Degraded($"Content safety answered {(int)response.StatusCode}; the word list screens comics instead.");
    }

    // ── Gemini remaster: lists the models (free). Remastering is off by default (Remaster:Enabled).
    private async Task<Outcome> RemasterAsync(HttpClient http, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>("Remaster:Enabled"))
        {
            return Outcome.SwitchedOff("Off (Remaster:Enabled is false)");
        }

        if (IsMock)
        {
            return Outcome.SwitchedOff("Mocked (no real AI calls)");
        }

        if (Setting("Google:GeminiApiKey") is not { } key)
        {
            return Outcome.Degraded("Remastering is on but the Gemini key is missing, so nothing is redrawn.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
        request.Headers.Add("x-goog-api-key", key);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? Outcome.Healthy()
            : Outcome.Degraded($"Gemini answered {(int)response.StatusCode}, so nothing is redrawn.");
    }

    // ── Key Vault: lists one secret NAME with the app's own credential (no value is read).
    private async Task<Outcome> KeyVaultAsync(HttpClient http, CancellationToken ct)
    {
        if (services.GetService<KeyVaultSource>() is not { } source)
        {
            return environment.IsProduction()
                ? Outcome.Unhealthy("Key Vault is not connected, so the app's secrets are missing.")
                : Outcome.SwitchedOff("Not used (local settings)");
        }

        await foreach (var page in source.Client.GetPropertiesOfSecretsAsync(ct).AsPages(pageSizeHint: 1).WithCancellation(ct))
        {
            _ = page;
            break;
        }

        return Outcome.Healthy("Listed secrets (live call)");
    }

    // ── Microsoft sign-in: reads the sign-in metadata, then asks for a token with the app's
    //    client secret. Only a rejected secret (invalid_client) or app (unauthorized_client) fails.
    private async Task<Outcome> MicrosoftSignInAsync(HttpClient http, CancellationToken ct)
    {
        var clientId = Setting("AzureAd:ClientId");
        var secret = Setting("AzureAd:ClientSecret");
        if (clientId is null || secret is null)
        {
            return environment.IsProduction()
                ? Outcome.Unhealthy("Microsoft sign-in is not configured, so nobody can sign in.")
                : Outcome.SwitchedOff("Guest sign-in only (local)");
        }

        using (var metadata = await http.GetAsync("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration", ct))
        {
            if (!metadata.IsSuccessStatusCode)
            {
                return Outcome.Unhealthy($"The Microsoft sign-in service answered {(int)metadata.StatusCode}.");
            }
        }

        using var token = await http.PostAsync("https://login.microsoftonline.com/common/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://graph.microsoft.com/.default"
        }), ct);
        var body = await token.Content.ReadAsStringAsync(ct);
        return body.Contains("\"invalid_client\"", StringComparison.Ordinal) || body.Contains("\"unauthorized_client\"", StringComparison.Ordinal)
            ? Outcome.Unhealthy("Microsoft rejected the app's sign-in secret, so nobody can sign in.")
            : Outcome.Healthy("Secret accepted (live call)");
    }

    // ── ntfy: the server's own health endpoint. Alerts are off when no topic is configured.
    private async Task<Outcome> OwnerAlertsAsync(HttpClient http, CancellationToken ct)
    {
        if (Setting($"{ModerationOptions.SectionName}:{nameof(ModerationOptions.AlertNtfyTopic)}") is null)
        {
            return Outcome.SwitchedOff("Off (no topic configured)");
        }

        var server = Setting($"{ModerationOptions.SectionName}:{nameof(ModerationOptions.AlertNtfyServer)}") ?? "https://ntfy.sh";
        using var response = await http.GetAsync($"{server.TrimEnd('/')}/v1/health", ct);
        return response.IsSuccessStatusCode
            ? Outcome.Healthy()
            : Outcome.Degraded($"The alert server answered {(int)response.StatusCode}, so alerts may not arrive.");
    }

    // ── Application Insights: an empty batch to the ingestion endpoint (nothing is recorded).
    private async Task<Outcome> TelemetryAsync(HttpClient http, CancellationToken ct)
    {
        if (PoPlatform.ResolveAppInsightsConnectionString(configuration) is not { } connectionString)
        {
            return Outcome.SwitchedOff("Off (no connection string)");
        }

        var ingestion = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1])
            .FirstOrDefault() ?? "https://dc.services.visualstudio.com/";

        using var response = await http.PostAsync($"{ingestion.TrimEnd('/')}/v2.1/track", new StringContent("[]", Encoding.UTF8, "application/json"), ct);
        return (int)response.StatusCode < 500
            ? Outcome.Healthy()
            : Outcome.Degraded($"Application Insights answered {(int)response.StatusCode}, so telemetry may be lost.");
    }
}

/// <summary>
/// Registered by <see cref="KeyVaultConfigurationExtensions"/> only when the app really loaded
/// its secrets from Key Vault, so <c>/diag</c> probes the same vault with the same credential.
/// </summary>
public sealed record KeyVaultSource(SecretClient Client);
