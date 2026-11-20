using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Configuration;
using PoSeeReview.Api.Testing;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

/// <summary>
/// Builds the anonymous <c>GET /api/diag</c> report (<see cref="DiagReportDto"/>) from the
/// registered health checks plus a few configuration-presence checks (AI, Key Vault, sign-in)
/// that have no free network probe.
/// <para>
/// The endpoint is anonymous in every environment, so this class is the safety boundary: it
/// emits fixed labels and purposes, statuses and timings only. A health check's own description
/// is never copied (it could name a host), and a failure names its exception TYPE, never the
/// message or stack. No configuration value is read into the response.
/// </para>
/// </summary>
public class DiagReportHandler(
    IConfiguration configuration,
    HealthCheckService healthCheckService,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<DiagReportHandler> logger)
{
    /// <summary>The <c>YYMMDD</c> BuildVersion Directory.Build.props stamps on every assembly.</summary>
    internal static readonly string Version =
        typeof(DiagReportHandler).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "unknown";

    /// <summary>Name, group, label and purpose for each registered health check (Program.cs).</summary>
    private static readonly Dictionary<string, (string Name, string Group, string Label, string Purpose)> KnownChecks = new()
    {
        ["azure_table_storage"] = ("table-storage", DiagGroups.Data, "Table Storage", "Holds comics, the Hall of Fame, budgets and reports."),
        ["azure_blob_storage"] = ("blob-storage", DiagGroups.Data, "Blob Storage", "Holds comic images and recorded audio."),
        ["google_maps_api"] = ("google-maps", DiagGroups.Service, "Google Maps", "Finds places and reads their public reviews."),
        ["skia_imaging"] = ("image-rendering", DiagGroups.Service, "Image rendering", "Draws captions onto comics and builds share cards.")
    };

    public async Task<DiagReportDto> ExecuteAsync(CancellationToken cancellationToken)
    {
        var isMock = MockDataMode.IsEnabled(configuration, environment);
        var started = timeProvider.GetTimestamp();
        var checks = new List<DiagCheckDto>
        {
            // Answering this request is the check; the page measures the round trip itself.
            new("api", DiagGroups.Api, "App server", nameof(HealthStatus.Healthy),
                "Answers every request the app makes.", "Answering", 0, null)
        };

        try
        {
            var report = await healthCheckService.CheckHealthAsync(cancellationToken);
            foreach (var (key, entry) in report.Entries)
            {
                var (name, group, label, purpose) = KnownChecks.TryGetValue(key, out var known)
                    ? known
                    : (key, DiagGroups.Service, key, "Internal dependency.");

                checks.Add(new DiagCheckDto(
                    name,
                    group,
                    label,
                    entry.Status.ToString(),
                    purpose,
                    DescribeProbe(key, entry.Status, isMock),
                    Math.Round(entry.Duration.TotalMilliseconds, 1),
                    entry.Status == HealthStatus.Healthy ? null : ProblemFor(key, entry.Status, entry.Exception)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to gather dependency status for the diag report");
            checks.Add(new DiagCheckDto(
                "health-checks",
                DiagGroups.Service,
                "Health checks",
                nameof(HealthStatus.Unhealthy),
                "Runs every dependency check on this page.",
                "The checks could not run",
                0,
                $"The checks could not run ({ex.GetType().Name})."));
        }

        checks.AddRange(ConfigurationChecks(isMock));

        var overall = checks.Min(c => Enum.TryParse<HealthStatus>(c.Status, out var s) ? s : HealthStatus.Unhealthy);

        return new DiagReportDto(
            overall.ToString(),
            environment.EnvironmentName,
            Version,
            timeProvider.GetUtcNow(),
            Math.Round(timeProvider.GetElapsedTime(started).TotalMilliseconds, 1),
            [.. checks]);
    }

    private static string DescribeProbe(string key, HealthStatus status, bool isMock) => key switch
    {
        // The Maps check reads configuration only — a geocode probe would be a billed call.
        "google_maps_api" when isMock => "Mocked (sample places)",
        "google_maps_api" => status == HealthStatus.Healthy ? "Key configured" : "Key not configured",
        _ => status switch
        {
            HealthStatus.Healthy => "Working",
            HealthStatus.Degraded => "Partly working",
            _ => "Not reachable"
        }
    };

    private static string ProblemFor(string key, HealthStatus status, Exception? exception)
    {
        var text = key == "google_maps_api"
            ? "The Google Maps key is not configured, so nearby places cannot be found."
            : status == HealthStatus.Degraded
                ? "Answering, but slowly or only in part."
                : "Not reachable, so features that need it will fail.";
        return exception is null ? text : $"{text} ({exception.GetType().Name})";
    }

    /// <summary>
    /// Services with no cheap, free probe: reported from whether they are configured. A missing
    /// value is Unhealthy where it is required (AI always; Key Vault and sign-in in Production)
    /// and Degraded otherwise.
    /// </summary>
    private IEnumerable<DiagCheckDto> ConfigurationChecks(bool isMock)
    {
        var isProduction = environment.IsProduction();

        var chatProvider = configuration[InfrastructureServiceCollectionExtensions.AiChatProviderConfigurationKey];
        var usesOllama = string.Equals(chatProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
        var azureAiConfigured = !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"])
            && !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:ApiKey"]);
        yield return Configured(
            "ai",
            "AI scoring and drawing",
            "Rates how strange the reviews are, writes the panels and draws the comic.",
            isMock ? (true, "Mocked (no real AI calls)") : usesOllama ? (true, "Local model (Ollama)") : (azureAiConfigured, azureAiConfigured ? "Configured" : "Not configured"),
            required: true,
            problem: "The AI service is not configured, so no comic can be made.");

        var keyVaultConfigured = !string.IsNullOrWhiteSpace(configuration["KeyVault:Uri"]);
        yield return Configured(
            "key-vault",
            "Key Vault",
            "Supplies the app's secrets at startup.",
            (keyVaultConfigured || !isProduction, keyVaultConfigured ? "Configured" : "Not used (local settings)"),
            required: isProduction,
            problem: "Key Vault is not configured, so the app's secrets are missing.");

        var signInConfigured = !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientId"])
            && !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientSecret"]);
        yield return Configured(
            "microsoft-sign-in",
            "Microsoft sign-in",
            "Lets people sign in with a Microsoft account.",
            (signInConfigured || !isProduction, signInConfigured ? "Configured" : "Guest sign-in only (local)"),
            required: isProduction,
            problem: "Microsoft sign-in is not configured, so nobody can sign in.");

        static DiagCheckDto Configured(string name, string label, string purpose, (bool Ok, string Detail) state, bool required, string problem)
        {
            var status = state.Ok ? HealthStatus.Healthy : required ? HealthStatus.Unhealthy : HealthStatus.Degraded;
            return new DiagCheckDto(name, DiagGroups.Service, label, status.ToString(), purpose, state.Detail, 0,
                state.Ok ? null : problem);
        }
    }
}
