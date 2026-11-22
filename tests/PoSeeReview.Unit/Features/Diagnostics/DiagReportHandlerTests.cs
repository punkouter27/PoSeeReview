using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PoSeeReview.Api.Features.Diagnostics;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Unit.Features.Diagnostics;

public class DiagReportHandlerTests
{
    private readonly Mock<HealthCheckService> _healthCheckServiceMock = new();

    /// <summary>What every probe's HTTP call answers; the unit tier never leaves the process.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage> _answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };

    private DiagReportHandler BuildSut(string environmentName = "Development", Dictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://secret-host.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "sk-very-secret-value",
                ["KeyVault:Uri"] = "https://kv-secret.vault.azure.net/"
            })
            .Build();

        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new StubHandler(r => _answer(r))));
        var probes = new ServiceProbes(factory.Object, config, environment, TimeProvider.System,
            Mock.Of<IServiceProvider>(), NullLogger<ServiceProbes>.Instance);

        return new DiagReportHandler(
            _healthCheckServiceMock.Object,
            environment,
            TimeProvider.System,
            probes,
            NullLogger<DiagReportHandler>.Instance);
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
        Assert.False(string.IsNullOrWhiteSpace(result.Version));
        var api = Assert.Single(result.Checks, c => c.Group == DiagGroups.Api);
        Assert.Equal("Healthy", api.Status);
        var table = Assert.Single(result.Checks, c => c.Group == DiagGroups.Data);
        Assert.Equal("Table Storage", table.Label);
        Assert.Equal("Healthy", table.Status);
        Assert.Equal(12, table.LatencyMs);
        Assert.Null(table.Problem);
        Assert.Contains(result.Checks, c => c.Label == "AI scoring and drawing" && c.Status == "Healthy");
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
        Assert.Contains(result.Checks, c => c.Problem is { } p && p.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
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
        Assert.Contains(result.Checks, c => c.Label == "AI scoring and drawing" && c.Status == "Unhealthy" && c.Problem is not null);
    }

    [Fact]
    public async Task ExecuteAsync_WhenHealthCheckThrows_ReportsTheTypeOnly()
    {
        _healthCheckServiceMock
            .Setup(x => x.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("health check failed at secret-host"));

        var result = await BuildSut().ExecuteAsync(default);

        var failed = Assert.Single(result.Checks, c => c.Label == "Health checks");
        Assert.Equal("Unhealthy", failed.Status);
        Assert.Contains(nameof(InvalidOperationException), failed.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ServicesSwitchedOffOnPurpose_AreOff_AndLeftOutOfTheOverallStatus()
    {
        SetupHealth([]);

        // Development, no Key Vault, no sign-in secret, no ntfy topic, no telemetry, remaster off.
        _answer = request => new HttpResponseMessage(request.RequestUri!.Host.Contains("places", StringComparison.Ordinal)
            ? System.Net.HttpStatusCode.BadRequest
            : System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{}") };
        var result = await BuildSut(settings: new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://ai.example/",
            ["AzureOpenAI:ApiKey"] = "k",
            ["GoogleMaps:ApiKey"] = "g"
        }).ExecuteAsync(default);

        foreach (var name in new[] { "key-vault", "microsoft-sign-in", "owner-alerts", "telemetry", "remaster", "content-safety" })
        {
            var row = Assert.Single(result.Checks, c => c.Name == name);
            Assert.Equal(ServiceProbes.Off, row.Status);
            Assert.Equal(0, row.LatencyMs);
            Assert.Null(row.Problem);
        }

        Assert.Equal("Healthy", result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ProbesTheAiServiceLive_AndReportsARejectedKey()
    {
        SetupHealth([]);
        var calls = new List<string>();
        _answer = request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(request.RequestUri.AbsolutePath.Contains("/openai/models", StringComparison.Ordinal)
                ? System.Net.HttpStatusCode.Unauthorized
                : System.Net.HttpStatusCode.BadRequest)
            { Content = new StringContent("{}") };
        };

        var result = await BuildSut(settings: new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = "https://ai.example/",
            ["AzureOpenAI:ApiKey"] = "k",
            ["GoogleMaps:ApiKey"] = "g"
        }).ExecuteAsync(default);

        Assert.Contains("/openai/models", calls);
        Assert.Contains("/v1/places:searchText", calls);
        Assert.Equal("Unhealthy", Assert.Single(result.Checks, c => c.Name == "ai").Status);
        Assert.Equal("Healthy", Assert.Single(result.Checks, c => c.Name == "google-maps").Status);
        Assert.Equal("Unhealthy", result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_GoogleMapsKeyRejected_IsUnhealthy()
    {
        SetupHealth([]);
        _answer = request => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(request.RequestUri!.Host.Contains("places", StringComparison.Ordinal) ? "{\"reason\":\"API_KEY_INVALID\"}" : "{}")
        };

        var result = await BuildSut(settings: new Dictionary<string, string?> { ["GoogleMaps:ApiKey"] = "bad" }).ExecuteAsync(default);

        Assert.Equal("Unhealthy", Assert.Single(result.Checks, c => c.Name == "google-maps").Status);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private void SetupHealth(Dictionary<string, HealthReportEntry> entries)
    {
        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(1));
        _healthCheckServiceMock
            .Setup(x => x.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);
    }
}
