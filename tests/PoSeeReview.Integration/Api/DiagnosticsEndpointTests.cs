using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoSeeReview.Api;
using PoSeeReview.Shared.Dtos;
using Xunit;

namespace PoSeeReview.Integration.Api;

/// <summary>
/// <c>GET /api/diag</c> is anonymous in every environment, so its body is checked against the
/// app's own configuration: no secret, connection string, URI, tenant id, path, email or
/// exception text may appear in it — only statuses, timings and fixed descriptions.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Api")]
public partial class DiagnosticsEndpointTests(CustomWebApplicationFactory<Program> factory)
    : IClassFixture<CustomWebApplicationFactory<Program>>
{
    /// <summary>Config keys whose values must never surface on /api/diag.</summary>
    private static readonly string[] SensitiveKeys =
    [
        "AzureOpenAI:Endpoint", "AzureOpenAI:ApiKey", "GoogleMaps:ApiKey", "Google:GeminiApiKey",
        "ConnectionStrings:AzureTableStorage", "ConnectionStrings:AzureBlobStorage",
        "AzureAd:ClientId", "KeyVault:Uri", "ApplicationInsights:ConnectionString"
    ];

    [DockerFact]
    public async Task Diag_IsAnonymous_AndContainsNoSecretsOrIdentifyingInternals()
    {
        var response = await factory.CreateClient().GetAsync("/api/diag");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        var report = JsonSerializer.Deserialize<DiagReportDto>(body, JsonSerializerOptions.Web);
        Assert.NotNull(report);
        Assert.Equal("Test", report.Environment);
        Assert.Matches("^[0-9]{6}$", report.Version);
        Assert.Contains(report.Checks, c => c.Group == DiagGroups.Api && c.Status == "Healthy");
        Assert.Contains(report.Checks, c => c.Group == DiagGroups.Data && c.Status == "Healthy");

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        foreach (var key in SensitiveKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var marker in new[] { "AccountKey", "DefaultEndpointsProtocol", "http://", "https://", "127.0.0.1", "localhost", "\\\\", "   at ", "StackTrace" })
        {
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(GuidPattern(), body);
        Assert.DoesNotMatch(EmailPattern(), body);
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex EmailPattern();
}
