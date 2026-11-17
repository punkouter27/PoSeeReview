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
/// Builds the anonymous <c>GET /api/diag</c> snapshot from the registered health checks plus a
/// few configuration-presence checks (AI, Key Vault, sign-in) that have no network probe.
/// <para>
/// The endpoint is anonymous in every environment, so this class is the safety boundary: it
/// emits fixed display names and purposes, statuses and timings only. A health check's own
/// description is never copied (it could name a host), and a failure carries its exception TYPE,
/// never the message or stack. No configuration value is read into the response.
/// </para>
/// </summary>
public class DiagnosticsSnapshotQueryHandler(
    IConfiguration configuration,
    HealthCheckService healthCheckService,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<DiagnosticsSnapshotQueryHandler> logger)
{
    /// <summary>The <c>YYMMDD</c> stamp Directory.Build.targets puts on every assembly.</summary>
    private static readonly string BuildStamp =
        typeof(DiagnosticsSnapshotQueryHandler).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "unknown";

    /// <summary>Display name, kind and purpose for each registered health check (Program.cs).</summary>
    private static readonly Dictionary<string, (string Kind, string Name, string Purpose)> KnownChecks = new()
    {
        ["azure_table_storage"] = (DiagnosticsCheckKinds.Storage, "Table Storage", "Holds comics, the Hall of Fame, budgets and reports."),
        ["azure_blob_storage"] = (DiagnosticsCheckKinds.Storage, "Blob Storage", "Holds comic images and recorded audio."),
        ["google_maps_api"] = (DiagnosticsCheckKinds.Service, "Google Maps", "Finds places and reads their public reviews."),
        ["skia_imaging"] = (DiagnosticsCheckKinds.Service, "Image rendering", "Draws captions onto comics and builds share cards.")
    };

    public async Task<DiagnosticsSnapshotDto> ExecuteAsync(CancellationToken cancellationToken)
    {
        var isMock = MockDataMode.IsEnabled(configuration, environment);
        var checks = new List<DiagnosticsCheckDto>();
        var started = timeProvider.GetTimestamp();

        try
        {
            var report = await healthCheckService.CheckHealthAsync(cancellationToken);
            var checkedAt = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var (key, entry) in report.Entries)
            {
                var (kind, name, purpose) = KnownChecks.TryGetValue(key, out var known)
                    ? known
                    : (DiagnosticsCheckKinds.Service, key, "Internal dependency.");

                checks.Add(new DiagnosticsCheckDto
                {
                    Kind = kind,
                    Name = name,
                    Purpose = purpose,
                    Status = entry.Status.ToString(),
                    Detail = DescribeProbe(key, entry.Status, isMock),
                    DurationMilliseconds = Math.Round(entry.Duration.TotalMilliseconds, 1),
                    ErrorType = entry.Exception?.GetType().Name,
                    CheckedAt = checkedAt
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to gather dependency status for diagnostics snapshot");
            checks.Add(new DiagnosticsCheckDto
            {
                Kind = DiagnosticsCheckKinds.Service,
                Name = "Health checks",
                Purpose = "Runs every dependency check above.",
                Status = nameof(HealthStatus.Unhealthy),
                Detail = "The checks could not run",
                ErrorType = ex.GetType().Name,
                CheckedAt = timeProvider.GetUtcNow().UtcDateTime
            });
        }

        checks.AddRange(ConfigurationChecks(isMock, timeProvider.GetUtcNow().UtcDateTime));

        var overall = checks.Count == 0
            ? HealthStatus.Healthy
            : checks.Min(c => Enum.TryParse<HealthStatus>(c.Status, out var s) ? s : HealthStatus.Unhealthy);

        return new DiagnosticsSnapshotDto
        {
            Timestamp = timeProvider.GetUtcNow().UtcDateTime,
            Environment = environment.EnvironmentName,
            BuildStamp = BuildStamp,
            Status = overall.ToString(),
            DurationMilliseconds = Math.Round(timeProvider.GetElapsedTime(started).TotalMilliseconds, 1),
            IsMockData = isMock,
            Checks = checks
        };
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

    /// <summary>
    /// Services with no cheap, free probe: reported from whether they are configured. A missing
    /// value is Degraded in Production; locally Key Vault and Microsoft sign-in are optional.
    /// </summary>
    private IEnumerable<DiagnosticsCheckDto> ConfigurationChecks(bool isMock, DateTime checkedAt)
    {
        var isProduction = environment.IsProduction();

        var chatProvider = configuration[InfrastructureServiceCollectionExtensions.AiChatProviderConfigurationKey];
        var usesOllama = string.Equals(chatProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
        var azureAiConfigured = !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"])
            && !string.IsNullOrWhiteSpace(configuration["AzureOpenAI:ApiKey"]);
        yield return Configured(
            "AI scoring and drawing",
            "Rates how strange the reviews are, writes the panels and draws the comic.",
            isMock ? Mocked() : usesOllama ? (true, "Local model (Ollama)") : (azureAiConfigured, azureAiConfigured ? "Configured" : "Not configured"),
            required: true);

        var keyVaultConfigured = !string.IsNullOrWhiteSpace(configuration["KeyVault:Uri"]);
        yield return Configured(
            "Key Vault",
            "Supplies the app's secrets at startup.",
            (keyVaultConfigured || !isProduction, keyVaultConfigured ? "Configured" : "Not used (local settings)"),
            required: isProduction);

        var signInConfigured = !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientId"])
            && !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientSecret"]);
        yield return Configured(
            "Microsoft sign-in",
            "Lets people sign in with a Microsoft account.",
            (signInConfigured || !isProduction, signInConfigured ? "Configured" : "Guest sign-in only (local)"),
            required: isProduction);

        DiagnosticsCheckDto Configured(string name, string purpose, (bool Ok, string Detail) state, bool required) => new()
        {
            Kind = DiagnosticsCheckKinds.Service,
            Name = name,
            Purpose = purpose,
            Status = (state.Ok ? HealthStatus.Healthy : required ? HealthStatus.Unhealthy : HealthStatus.Degraded).ToString(),
            Detail = state.Detail,
            CheckedAt = checkedAt
        };

        static (bool, string) Mocked() => (true, "Mocked (no real AI calls)");
    }
}
