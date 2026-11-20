using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Middleware;
using Testcontainers.Azurite;
using PoSeeReview.Api.Testing;

namespace PoSeeReview.Integration;

/// <summary>
/// Custom WebApplicationFactory that spins up a dedicated Testcontainers Azurite instance
/// so the WebApp can reach real Azure Storage emulation during tests.
/// Implements IAsyncLifetime: xUnit calls InitializeAsync BEFORE any test class is created,
/// so the container is running and env vars are set before WebApp.CreateBuilder() reads them.
/// </summary>
public class CustomWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram>, IAsyncLifetime
    where TProgram : class
{
    // One container per factory instance (one per IClassFixture<> type)
    private AzuriteContainer? _azuriteContainer;

    // Connection string captured after container starts; used by ConfigureWebHost to wire DI correctly.
    private string? _azuriteConnectionString;

    // Unique 8-char hex prefix per factory instance — prevents table name collisions when multiple
    // test classes run in parallel against the same Azurite process.
    private readonly string _tablePrefix = Guid.NewGuid().ToString("N")[..8];

    public CustomWebApplicationFactory()
    {
        // Env vars that must be present BEFORE WebApplication.CreateBuilder(args) runs because
        // AddInfrastructure reads builder.Configuration during service registration — before
        // ConfigureAppConfiguration callbacks fire.
        // Storage connection strings are set in InitializeAsync once the container is ready.
        SetIfEmpty("DISABLE_SERILOG", "true");
        SetIfEmpty("DISABLE_USER_AGENT_VALIDATION", "true");

        // External API stubs — allow AddInfrastructure to pass its required-key checks.
        // Tests that validate real AI/Maps calls are expected to skip via HasValidApiKey guards.
        SetIfEmpty("AzureOpenAI__Endpoint", "https://test-openai.openai.azure.com");
        SetIfEmpty("AzureOpenAI__ApiKey", "test-api-key-00000000000000000000000000000000");
        SetIfEmpty("AzureOpenAI__DeploymentName", "gpt-5.4-nano");
        SetIfEmpty("AzureOpenAI__DalleDeploymentName", "");
        SetIfEmpty("GoogleMaps__ApiKey", "test-google-maps-api-key");
    }

    // -------------------------------------------------------------------------
    // IAsyncLifetime — xUnit lifecycle hooks
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the Azurite container and sets connection-string env vars before
    /// any test class is constructed and before CreateClient() is called.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return; // Every test in this tier is skipped; there is no Docker to start Azurite in.
        }

        // --skipApiVersionCheck: the Azure SDK's storage API version can be newer than
        // the Azurite release; the emulator still supports the operations we use.
        _azuriteContainer = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("--skipApiVersionCheck")
            .Build();
        await _azuriteContainer.StartAsync();

        _azuriteConnectionString = _azuriteContainer.GetConnectionString();

        // Also set env vars so that AddInfrastructure picks up the right CS if it reads them
        // before ConfigureAppConfiguration fires (timing-safe double-coverage).
        Environment.SetEnvironmentVariable("ConnectionStrings__AzureTableStorage", _azuriteConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__AzureBlobStorage", _azuriteConnectionString);
    }

    /// <summary>
    /// Disposes the WebApp host, then guarantees the Azurite container is torn down
    /// immediately post-run — even if host disposal throws (directive #3 / lifecycle).
    /// </summary>
    public new async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_azuriteContainer != null)
            {
                await _azuriteContainer.DisposeAsync();
                _azuriteContainer = null;
            }
        }
    }

    private static void SetIfEmpty(string key, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ASPNETCORE_URLS", "http://localhost");
        builder.UseEnvironment("Test");

        // Intercept AI provider calls at the HTTP boundary so the real service classes
        // can be exercised without spending tokens / leaking cost (directive #6).
        builder.ConfigureServices((context, services) =>
        {
            services.AddMockedAiBoundaries(context.Configuration);
        });

        // Use simple console logging for tests
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        // Add test configuration with defaults for required services
        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Provide default test configuration that satisfies all required dependencies
            var testConfig = new Dictionary<string, string?>
            {
                // Azure Storage — use the per-factory Testcontainers Azurite instance so each
                // factory class gets its own isolated storage process.
                ["ConnectionStrings:AzureTableStorage"] = _azuriteConnectionString ?? "UseDevelopmentStorage=true",
                ["ConnectionStrings:AzureBlobStorage"] = _azuriteConnectionString ?? "UseDevelopmentStorage=true",

                // Isolate table names per factory instance to prevent cross-test-class state
                // pollution when multiple IClassFixture<> factories run in parallel.
                ["AzureStorage:LeaderboardTableName"] = $"Test{_tablePrefix}Leaderboard",
                ["AzureStorage:ComicsTableName"] = $"Test{_tablePrefix}Comics",
                ["AzureStorage:RestaurantsTableName"] = $"Test{_tablePrefix}Restaurants",

                // Google Maps - use placeholder for tests that don't call real API
                ["GoogleMaps:ApiKey"] = "test-google-maps-api-key",

                // Without a key the image service cannot be constructed, so the whole comic
                // pipeline failed in DI and no test here ever reached it. The call itself is
                // answered by AiMockDelegatingHandler.
                ["Google:GeminiApiKey"] = "test-gemini-api-key"
            };

            // Override with environment variables if explicitly set
            if (Environment.GetEnvironmentVariable("AZURE_TABLE_STORAGE_CONNECTION_STRING") is { } tableStorage)
                testConfig["ConnectionStrings:AzureTableStorage"] = tableStorage;

            if (Environment.GetEnvironmentVariable("AZURE_BLOB_STORAGE_CONNECTION_STRING") is { } blobStorage)
                testConfig["ConnectionStrings:AzureBlobStorage"] = blobStorage;

            if (Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") is { } openAiEndpoint)
                testConfig["AzureOpenAI:Endpoint"] = openAiEndpoint;

            if (Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") is { } openAiKey)
                testConfig["AzureOpenAI:ApiKey"] = openAiKey;

            if (Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") is { } googleMapsKey)
                testConfig["GoogleMaps:ApiKey"] = googleMapsKey;

            config.AddInMemoryCollection(testConfig);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        // Set a valid browser user agent to pass UserAgentValidationMiddleware
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // Endpoints now deny by default (NET_RULES 4.1/4.5). Authenticate via the Test-only
        // FakeAuth scheme (§4.4 guest bypass) so business-endpoint calls carry an authenticated
        // principal instead of hitting the fallback policy with 401.
        client.DefaultRequestHeaders.Add("X-Fake-User", "integration-test-user");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "User");
    }
}
