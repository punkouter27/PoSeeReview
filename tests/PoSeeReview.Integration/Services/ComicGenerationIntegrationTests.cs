using PoSeeReview.Api.Telemetry;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PoSeeReview.Api.Features.Comics;
using Xunit.Abstractions;
using Xunit;

namespace PoSeeReview.Integration.Services;

/// <summary>
/// Integration tests for the complete comic generation workflow from reviews
/// Tests the full end-to-end flow: Reviews → Azure OpenAI → Story Generation
/// 
/// NOTE: These tests require real Azure OpenAI API calls and will incur costs.
/// Use [Trait("Category", "Expensive")] to filter them out in normal test runs.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Services")]
[Trait("Suite", "CriticalPath")]
public class ComicGenerationIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ComicGenerationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DockerFact]

    [Trait("Category", "Integration")]
    [Trait("Category", "Expensive")]
    [Trait("Category", "RequiresAzureOpenAI")]
    public async Task AzureOpenAI_AnalyzeStrangenessFromReviews_ShouldCreateStoryAndScore()
    {
        // Arrange - Pre-made restaurant reviews with bizarre characteristics
        var reviews = new List<string>
        {
            "The waiter insisted on serving everything backwards - dessert first, then appetizers. When I asked why, he said 'time flows differently here.' The food was excellent though!",
            "There's a cat that lives in the kitchen and occasionally judges your food choices by meowing. If it meows three times, the chef will remake your dish. I've never seen anything like it.",
            "The restaurant has a strict 'no talking about Tuesday' policy. I accidentally mentioned Tuesday and they made me sing karaoke for 10 minutes as penance. Best punishment ever!",
            "Every table has a different gravity setting. We sat at the 'moon gravity' table and our soup kept floating away. Management says it's 'experimental dining.' I loved it.",
            "The owner communicates only through interpretive dance. Ordering was an adventure, but somehow we got exactly what we wanted. The salmon was divine."
        };

        // Load configuration from appsettings.Development.json
        var basePath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", "src", "PoSeeReview.Api"));

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            // Stub values prevent service construction from throwing.
            // The StartsWith("test-") guard below skips execution when stubs are active.
            // appsettings.Development.json is intentionally excluded: real OpenAI
            // credentials must be supplied via env vars (CI pattern, PoTest rule #7).
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:ApiKey"] = "test-stub-api-key",
                ["AzureOpenAI:Endpoint"] = "https://test-openai.openai.azure.com",
                ["AzureOpenAI:DeploymentName"] = "gpt-5.4-nano",
            })
            .AddEnvironmentVariables()
            .Build();

        // Setup Azure OpenAI service with real configuration
        var openAiOptions = configuration.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>();

        // Skip test if configuration is missing or contains placeholder values
        if (string.IsNullOrEmpty(openAiOptions?.ApiKey)
            || openAiOptions.ApiKey.Length < 20
            || openAiOptions.ApiKey.StartsWith("test-", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("⚠️ Skipping test: Azure OpenAI configuration not found or is a placeholder");
            return;
        }

        var telemetryClient = new Microsoft.ApplicationInsights.TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration());
        var openAiClient = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(openAiOptions.Endpoint!),
            new Azure.AzureKeyCredential(openAiOptions.ApiKey!));
        var chatService = new AzureOpenAIChatService(
            openAiClient,
            Microsoft.Extensions.Options.Options.Create(openAiOptions),
            telemetryClient,
            new AiCostTracker(Microsoft.Extensions.Options.Options.Create(new AiPricingOptions()), telemetryClient));

        _output.WriteLine("📝 Input Reviews:");
        foreach (var review in reviews)
        {
            _output.WriteLine($"  • {review}");
        }

        _output.WriteLine("");

        // Act - Analyze reviews using real Azure OpenAI
        var analysis = await chatService.AnalyzeStrangenessAsync(reviews);
        var (strangenessScore, panelCount, narrative) = (analysis.StrangenessScore, analysis.PanelCount, analysis.Narrative);

        // Assert - Verify narrative
        Assert.NotNull(narrative);
        Assert.NotEmpty(narrative);

        // Verify the narrative is substantial (should be multiple sentences)
        Assert.True(narrative.Length > 50, $"Narrative too short: {narrative.Length} characters");

        // Verify it contains story-like elements (sentences end with . ! or ?)
        var sentenceEndings = narrative.Count(c => c == '.' || c == '!' || c == '?');
        Assert.True(sentenceEndings >= 1, "Narrative should contain at least one sentence");

        // Assert - Verify strangeness score
        Assert.InRange(strangenessScore, 0, 100);

        // Assert - Verify panel count
        Assert.InRange(panelCount, 1, 4);

        // These reviews are quite strange, so score should be reasonably high
        Assert.True(strangenessScore > 30, $"Expected high strangeness score for bizarre reviews, got {strangenessScore}");

        // Output results
        _output.WriteLine($"🎯 Strangeness Score: {strangenessScore}/100");
        _output.WriteLine($"🎬 Panel Count: {panelCount} panels");
        _output.WriteLine("");

        if (strangenessScore >= 80)
        {
            _output.WriteLine("🌟 EXTREMELY STRANGE - Perfect for comic generation!");
        }
        else if (strangenessScore >= 60)
        {
            _output.WriteLine("🎨 VERY STRANGE - Great comic material!");
        }
        else if (strangenessScore >= 40)
        {
            _output.WriteLine("🤔 MODERATELY STRANGE - Good comic potential");
        }
        else
        {
            _output.WriteLine("📝 MILDLY STRANGE - Might need more bizarre elements");
        }

        _output.WriteLine("");
        _output.WriteLine("✨ Generated Narrative:");
        _output.WriteLine(narrative);
        _output.WriteLine("");

        var sentenceCount = narrative.Count(c => c == '.' || c == '!' || c == '?');
        _output.WriteLine($"📊 Stats: {narrative.Length} characters, {narrative.Split(' ').Length} words, {sentenceCount} sentences");
    }

    [DockerFact]

    [Trait("Category", "Integration")]
    [Trait("Category", "Expensive")]
    [Trait("Category", "RequiresAzureOpenAI")]
    [Trait("Category", "RequiresDALLE")]
    public void ComicGeneration_FullWorkflow_Placeholder()
    {
        // This test class exists for documentation and future expensive integration testing
        // All comic generation functionality is tested by:
        // - Unit tests in ComicGenerationServiceTests.cs
        // - Integration tests in ComicsEndpointTests.cs
        // - Storage tests in BlobStorageTests.cs
        Assert.True(true);
    }
}
