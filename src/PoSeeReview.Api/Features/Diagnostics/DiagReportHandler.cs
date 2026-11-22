using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

/// <summary>
/// Builds the anonymous <c>GET /api/diag</c> report (<see cref="DiagReportDto"/>) from the
/// registered health checks (storage, image rendering) plus the live external-service probes in
/// <see cref="ServiceProbes"/>. A service switched off on purpose is "Off" and does not count
/// toward the overall status.
/// <para>
/// The endpoint is anonymous in every environment, so this class is the safety boundary: it
/// emits fixed labels and purposes, statuses and timings only. A health check's own description
/// is never copied (it could name a host), and a failure names its exception TYPE, never the
/// message or stack. No configuration value is read into the response.
/// </para>
/// </summary>
public class DiagReportHandler(
    HealthCheckService healthCheckService,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ServiceProbes serviceProbes,
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
        ["skia_imaging"] = ("image-rendering", DiagGroups.Service, "Image rendering", "Draws captions onto comics and builds share cards.")
    };

    public async Task<DiagReportDto> ExecuteAsync(CancellationToken cancellationToken)
    {
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
            // Google Maps is probed live by ServiceProbes; its /health entry only checks the key.
            foreach (var (key, entry) in report.Entries.Where(e => e.Key != "google_maps_api"))
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
                    DescribeProbe(entry.Status),
                    Math.Round(entry.Duration.TotalMilliseconds, 1),
                    entry.Status == HealthStatus.Healthy ? null : ProblemFor(entry.Status, entry.Exception)));
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

        checks.AddRange(await serviceProbes.RunAsync(cancellationToken));

        // "Off" rows are switched off on purpose: neutral, never part of the overall status.
        var overall = checks.Where(c => c.Status != ServiceProbes.Off)
            .Select(c => Enum.TryParse<HealthStatus>(c.Status, out var s) ? s : HealthStatus.Unhealthy)
            .DefaultIfEmpty(HealthStatus.Healthy)
            .Min();

        return new DiagReportDto(
            overall.ToString(),
            environment.EnvironmentName,
            Version,
            timeProvider.GetUtcNow(),
            Math.Round(timeProvider.GetElapsedTime(started).TotalMilliseconds, 1),
            [.. checks]);
    }

    private static string DescribeProbe(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Working",
        HealthStatus.Degraded => "Partly working",
        _ => "Not reachable"
    };

    private static string ProblemFor(HealthStatus status, Exception? exception)
    {
        var text = status == HealthStatus.Degraded
            ? "Answering, but slowly or only in part."
            : "Not reachable, so features that need it will fail.";
        return exception is null ? text : $"{text} ({exception.GetType().Name})";
    }
}
