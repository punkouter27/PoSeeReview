using PoSeeReview.Api.Configuration;

namespace PoSeeReview.Api.Testing;

/// <summary>
/// <c>UseMockData=true</c>: a local run that boots with no secrets at all. Google Maps is
/// replaced by <see cref="TestMockRestaurantService"/> and every AI call is answered by
/// <see cref="AiMockDelegatingHandler"/>, so nothing is billed and no Key Vault value is needed.
/// Storage stays real (Azurite), which is what makes the E2E UI suite and the browser checks
/// meaningful. Development and Test only — any other environment refuses to start with it.
/// </summary>
public static class MockDataMode
{
    public const string ConfigurationKey = "UseMockData";

    /// <summary>Placeholder AI endpoint. Its host ends in <c>openai.azure.com</c>, which the mock handler answers.</summary>
    private const string MockAzureOpenAIEndpoint = "https://mock.openai.azure.com/";

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool>(ConfigurationKey)
        && (environment.IsDevelopment() || environment.IsEnvironment(HostEnvironments.Test));

    /// <summary>
    /// Overrides the AI and Maps settings with placeholders. They are forced rather than
    /// defaulted: with the boundaries mocked, a real key in the environment would be unused at
    /// best, and the mock handler only recognises the placeholder host.
    /// </summary>
    public static void Apply(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>(ConfigurationKey))
        {
            return;
        }

        if (!IsEnabled(builder.Configuration, builder.Environment))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is a Development/Test-only switch; it cannot run in {builder.Environment.EnvironmentName}.");
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:ChatProvider"] = "AzureOpenAI",
            ["Ai:ImageProvider"] = "AzureOpenAI",
            ["AzureOpenAI:Endpoint"] = MockAzureOpenAIEndpoint,
            ["AzureOpenAI:ApiKey"] = "mock-key-00000000000000000000AB",
            ["AzureOpenAI:DeploymentName"] = "gpt-5.4-nano",
            // The FLUX route is not a shape the mock handler draws; gpt-image's is.
            ["AzureOpenAI:BlackForestLabsRoute"] = string.Empty,
            ["GoogleMaps:ApiKey"] = "mock-google-maps-key"
        });
    }
}
