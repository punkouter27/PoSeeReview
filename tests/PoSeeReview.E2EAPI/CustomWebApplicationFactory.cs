using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Api;
using Testcontainers.Azurite;
using PoSeeReview.Api.Testing;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.E2EAPI;

/// <summary>
/// Custom WebApplicationFactory that configures logging without Serilog
/// to avoid frozen logger issues with multiple test hosts.
/// Storage runs against an ephemeral Testcontainers Azurite instance (NET_RULES 6.4);
/// AI/Maps services stay mocked so no external tokens are spent.
/// </summary>
public class CustomWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram>, IAsyncLifetime
    where TProgram : class
{
    private AzuriteContainer? _azuriteContainer;
    private string? _azuriteConnectionString;

    public CustomWebApplicationFactory()
    {
        // Set environment variables BEFORE Program.cs runs (WebApplicationFactory reads
        // these when building the host, so they must be present before CreateBuilder runs).
        Environment.SetEnvironmentVariable("DISABLE_SERILOG", "true");
        Environment.SetEnvironmentVariable("DISABLE_USER_AGENT_VALIDATION", "true");

        // AzureOpenAI options — IConfiguration uses __ as the section separator for env vars
        Environment.SetEnvironmentVariable("AzureOpenAI__Endpoint", "https://test.openai.azure.com/");
        Environment.SetEnvironmentVariable("AzureOpenAI__ApiKey", "test-key-12345678901234567890AB");
        // Mirror the prod deployment name (verified 2026-06-14 as the sole deployment in po-aiservices-shared2).
        Environment.SetEnvironmentVariable("AzureOpenAI__DeploymentName", "gpt-5.4-nano");
        Environment.SetEnvironmentVariable("AzureOpenAI__DalleDeploymentName", "");
    }

    /// <summary>
    /// Starts the ephemeral Azurite container and publishes its connection string
    /// before the host is built (xUnit runs this before any test class is created).
    /// </summary>
    public async Task InitializeAsync()
    {
        // --skipApiVersionCheck: the Azure SDK's storage API version can be newer than
        // the Azurite release; the emulator still supports the operations we use.
        _azuriteContainer = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("--skipApiVersionCheck")
            .Build();
        await _azuriteContainer.StartAsync();

        _azuriteConnectionString = _azuriteContainer.GetConnectionString();
        Environment.SetEnvironmentVariable("ConnectionStrings__AzureTableStorage", _azuriteConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__AzureBlobStorage", _azuriteConnectionString);
    }

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) =>
        {
            // Remove Serilog logger if present
            var loggerDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILogger<>));
            if (loggerDescriptor != null)
            {
                services.Remove(loggerDescriptor);
            }

            // Replace real Azure-backed services with in-memory fakes, surfaced as
            // IMockable so /diag/mock-status lights the USING MOCK DATA banner.
            var fakeComics = new FakeComicGenerationService();
            var fakeRestaurants = new FakeRestaurantService();
            services.Replace(ServiceDescriptor.Singleton<IComicGenerationService>(fakeComics));
            services.Replace(ServiceDescriptor.Singleton<IRestaurantService>(fakeRestaurants));
            services.AddSingleton<IMockable>(fakeComics);
            services.AddSingleton<IMockable>(fakeRestaurants);

            // Defense-in-depth: intercept any AI provider HTTP calls so no tokens are
            // ever spent from the E2E API suite (directive #6).
            services.AddMockedAiBoundaries(context.Configuration);
        });

        // Use simple console logging for tests instead of Serilog
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        // Disable HTTPS redirection for tests
        builder.UseSetting("ASPNETCORE_URLS", "http://localhost");

        // Add test configuration with Azure connection strings
        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Add in-memory configuration with test values
            var testConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:AzureTableStorage"] = _azuriteConnectionString ?? "UseDevelopmentStorage=true",
                ["ConnectionStrings:AzureBlobStorage"] = _azuriteConnectionString ?? "UseDevelopmentStorage=true",
                ["AzureOpenAI:Endpoint"] = "https://test.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "test-key-12345",
                ["AzureOpenAI:DeploymentName"] = "test-deployment",
                ["AzureOpenAI:DalleDeploymentName"] = "test-dalle-deployment",
                ["GoogleMaps:ApiKey"] = "test-google-maps-key"
            };

            config.AddInMemoryCollection(testConfig);
        });
    }

    // ── Minimal in-memory fakes ──────────────────────────────────────────────

    /// <summary>
    /// Fake IComicGenerationService: cache always empty, generation always fails with KeyNotFoundException.
    /// </summary>
    private sealed class FakeComicGenerationService : IComicGenerationService, IMockable
    {
        public Task<Comic> GenerateComicAsync(
            PlaceId placeId,
            bool forceRegenerate = false,
            IProgress<ComicGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new KeyNotFoundException($"Restaurant not found in test environment: {placeId}");

        public Task<Comic?> GetCachedComicAsync(
            PlaceId placeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Comic?>(null);
    }

    /// <summary>
    /// Fake IRestaurantService: always returns empty lists and null details.
    /// </summary>
    private sealed class FakeRestaurantService : IRestaurantService, IMockable
    {
        public Task<List<Restaurant>> GetNearbyRestaurantsAsync(
            double latitude,
            double longitude,
            int limit = 10,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<Restaurant>());

        public Task<Restaurant> GetRestaurantByPlaceIdAsync(
            PlaceId placeId,
            CancellationToken cancellationToken = default)
            => throw new KeyNotFoundException($"Restaurant not found in test environment: {placeId}");

        public Task<(double lat, double lon)?> GeocodeLocationAsync(
            string locationQuery,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(double lat, double lon)?>(null);

        public Task<Restaurant?> GetRestaurantDetailsAsync(
            string placeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Restaurant?>(null);
    }
}
