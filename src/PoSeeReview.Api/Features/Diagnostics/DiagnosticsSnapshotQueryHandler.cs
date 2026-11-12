using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Identity;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Api.Features.Diagnostics;

public class DiagnosticsSnapshotQueryHandler(
    IConfiguration configuration,
    HealthCheckService healthCheckService,
    ICurrentRequestIdentityAccessor currentRequestIdentityAccessor,
    IHostEnvironment environment,
    ILogger<DiagnosticsSnapshotQueryHandler> logger)
{
    /// <summary>The <c>YYMMDD</c> stamp Directory.Build.targets puts on every assembly.</summary>
    private static readonly string BuildStamp =
        typeof(DiagnosticsSnapshotQueryHandler).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "unknown";

    private static readonly string[] SensitivePatterns =
    [
        "key", "secret", "password", "connectionstring", "token", "apikey", "credential", "clientsecret", "private"
    ];

    private static readonly string[] ExcludedKeyPrefixes =
    [
        "PATH", "PATHEXT", "PSModulePath", "TEMP", "TMP", "USERNAME", "USERPROFILE", "HOMEPATH", "LOCALAPPDATA", "APPDATA",
        "PROGRAMFILES", "PROGRAMDATA", "SYSTEMROOT", "WINDIR", "GIT_", "VSCODE_", "PROCESSOR_", "COMPUTERNAME"
    ];

    private static readonly string[] AllowedKeyPrefixes =
    [
        "AllowedHosts",
        "ApplicationInsights",
        "ASPNETCORE_ENVIRONMENT",
        "Azure",
        "Cleanup",
        "Comics",
        "ConnectionStrings",
        "DOTNET_",
        "Google",
        "HealthChecks",
        "KeyVault",
        "RateLimiting",
        "Serilog"
    ];

    private static readonly string[] DiagnosticKeyNames =
    [
        "KeyVault:Uri",
        "ApplicationInsights:ConnectionString",
        "GoogleMaps:ApiKey",
        "AzureOpenAI:Endpoint",
        "AzureOpenAI:ApiKey",
        "ConnectionStrings:Storage",
        "ConnectionStrings:AzureTableStorage",
        "ConnectionStrings:AzureBlobStorage"
    ];

    public async Task<DiagnosticsSnapshotDto> ExecuteAsync(CancellationToken cancellationToken)
    {
        // /diag is anonymous so platform probes can reach it, but the full configuration
        // surface (endpoint URLs, ClientId/tenant GUIDs, masked-but-partial secret prefixes)
        // must never leak in Production. Outside Development we return dependency health and
        // key *existence* only — no values, no config dump.
        var exposeConfigDetail = environment.IsDevelopment();

        var configEntries = new List<ConfigurationValueDto>();
        if (exposeConfigDetail)
        {
            foreach (var section in configuration.AsEnumerable())
            {
                if (ShouldExcludeKey(section.Key) || !ShouldIncludeKey(section.Key))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(section.Value))
                {
                    continue;
                }

                configEntries.Add(new ConfigurationValueDto
                {
                    Key = section.Key,
                    Value = MaskValue(section.Key, section.Value)
                });
            }
        }

        HealthReport? dependencyReport = null;
        try
        {
            dependencyReport = await healthCheckService.CheckHealthAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to gather dependency status for diagnostics snapshot");
        }

        var keyStatus = DiagnosticKeyNames
        .Select(key => new ConfigurationKeyStatusDto
        {
            Key = key,
            Exists = !string.IsNullOrWhiteSpace(configuration[key]),
            // Masked value (partial prefix/suffix) is still identifying — Development only.
            Value = exposeConfigDetail ? MaskValue(key, configuration[key]) : null
        })
        .ToList();

        return new DiagnosticsSnapshotDto
        {
            Timestamp = DateTime.UtcNow,
            Environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Unknown",
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            BuildStamp = BuildStamp,
            ProcessId = Environment.ProcessId,
            CurrentUserId = currentRequestIdentityAccessor.GetCurrentUserId().Value,
            DependencyStatus = dependencyReport == null
                ? null
                : new DependencyStatusSummaryDto
                {
                    Overall = dependencyReport.Status.ToString(),
                    Checks = dependencyReport.Entries.Select(entry => new DependencyCheckDto
                    {
                        Name = entry.Key,
                        Status = entry.Value.Status.ToString(),
                        Description = entry.Value.Description,
                        DurationMilliseconds = entry.Value.Duration.TotalMilliseconds,
                        // Full exception detail (types, stack) only outside Production — /diag is anonymous.
                        Error = environment.IsDevelopment() ? entry.Value.Exception?.ToString() : null
                    }).ToList()
                },
            KeyStatus = keyStatus,
            Config = configEntries.OrderBy(entry => entry.Key).ToList()
        };
    }

    private static string? MaskValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var lowerKey = key.ToLowerInvariant();
        var isSensitive = SensitivePatterns.Any(pattern => lowerKey.Contains(pattern));
        var looksLikeSecretValue =
            value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("gho_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("AIza", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase);

        if (isSensitive || looksLikeSecretValue)
        {
            return MaskMiddle(value);
        }

        if (value.Length > 120)
        {
            return string.Concat(value.AsSpan(0, 117), "...");
        }

        return value;
    }

    private static string MaskMiddle(string value)
    {
        if (value.Length <= 6)
        {
            return new string('*', value.Length);
        }

        return string.Concat(value.AsSpan(0, 3), "***", value.AsSpan(value.Length - 3));
    }

    private static bool ShouldExcludeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        return ExcludedKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIncludeKey(string key)
    {
        return AllowedKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
