using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PoSeeReview.Api.Features.Diagnostics;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Unit.Features.Diagnostics;

public class DiagnosticsSnapshotQueryHandlerTests
{
    private readonly Mock<HealthCheckService> _healthCheckServiceMock = new();

    private DiagnosticsSnapshotQueryHandler BuildSut(string environmentName = "Development", Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://secret-host.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "sk-very-secret-value",
                ["KeyVault:Uri"] = "https://kv-secret.vault.azure.net/"
            })
            .Build();

        return new DiagnosticsSnapshotQueryHandler(
            config,
            _healthCheckServiceMock.Object,
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName),
            TimeProvider.System,
            NullLogger<DiagnosticsSnapshotQueryHandler>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsEnvironment_AndNamesEachCheckInPlainEnglish()
    {
        SetupHealth(new Dictionary<string, HealthReportEntry>
        {
            ["azure_table_storage"] = new(HealthStatus.Healthy, "Reached https://acct.table.core.windows.net", TimeSpan.FromMilliseconds(12), null, null)
        });

        var result = await BuildSut().ExecuteAsync(default);

        Assert.Equal("Development", result.Environment);
        var table = Assert.Single(result.Checks, c => c.Kind == DiagnosticsCheckKinds.Storage);
        Assert.Equal("Table Storage", table.Name);
        Assert.Equal("Healthy", table.Status);
        Assert.Equal(12, table.DurationMilliseconds);
        Assert.Contains(result.Checks, c => c.Name == "AI scoring and drawing" && c.Status == "Healthy");
    }

    [Fact]
    public async Task ExecuteAsync_NeverCopiesDescriptions_ExceptionMessages_OrConfigValues()
    {
        SetupHealth(new Dictionary<string, HealthReportEntry>
        {
            ["azure_blob_storage"] = new(HealthStatus.Unhealthy, "Account https://leaky.blob.core.windows.net failed",
                TimeSpan.FromMilliseconds(5), new InvalidOperationException("AccountKey=abc123 at C:\\secret\\path"), null)
        });

        var result = await BuildSut().ExecuteAsync(default);
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.Equal("Unhealthy", result.Status);
        Assert.Contains(result.Checks, c => c.ErrorType == nameof(InvalidOperationException));
        foreach (var leak in new[] { "leaky", "AccountKey", "sk-very-secret", "secret-host", "kv-secret", "openai.azure.com", "vault.azure.net", "C:\\" })
        {
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingAiSettingsInProduction_IsUnhealthy()
    {
        SetupHealth([]);

        var result = await BuildSut("Production", []).ExecuteAsync(default);

        Assert.Equal("Unhealthy", result.Status);
        Assert.Contains(result.Checks, c => c.Name == "AI scoring and drawing" && c.Status == "Unhealthy");
    }

    [Fact]
    public async Task ExecuteAsync_WhenHealthCheckThrows_ReportsTheTypeOnly()
    {
        _healthCheckServiceMock
            .Setup(x => x.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("health check failed at secret-host"));

        var result = await BuildSut().ExecuteAsync(default);

        var failed = Assert.Single(result.Checks, c => c.Name == "Health checks");
        Assert.Equal("Unhealthy", failed.Status);
        Assert.Equal(nameof(InvalidOperationException), failed.ErrorType);
        Assert.DoesNotContain("secret-host", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private void SetupHealth(Dictionary<string, HealthReportEntry> entries)
    {
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(1));
        _healthCheckServiceMock
            .Setup(x => x.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);
    }
}
