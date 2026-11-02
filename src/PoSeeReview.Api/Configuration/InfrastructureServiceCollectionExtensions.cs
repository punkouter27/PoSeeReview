using PoSeeReview.Api.Features.Sharing;
using PoSeeReview.Api.Features.Comics.Painters;
using PoSeeReview.Api.Telemetry;
using System.Net;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Features.Diagnostics;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Api.HostedServices;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Configuration;

/// <summary>
/// Extension methods for registering infrastructure services
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Configuration path for <see cref="AiImageProvider"/>. A constant because the deploy
    /// workflow and appsettings must agree with this file exactly, and a typo in a raw string
    /// would silently select the default provider.
    /// </summary>
    public const string AiProviderConfigurationKey = "Ai:ImageProvider";

    /// <summary>Configuration path for <see cref="AiChatProvider"/>. Absent means Azure OpenAI.</summary>
    public const string AiChatProviderConfigurationKey = "Ai:ChatProvider";

    /// <summary>
    /// Configuration path for the premium painter that redraws popular comics. Absent means Gemini;
    /// the same value as <see cref="AiProviderConfigurationKey"/> means there is nothing to upgrade to.
    /// </summary>
    public const string AiRemasterProviderConfigurationKey = "Ai:RemasterProvider";

    /// <summary>The draft painter when <see cref="AiProviderConfigurationKey"/> is absent: the cheap one.</summary>
    public const AiImageProvider DefaultImageProvider = AiImageProvider.AzureOpenAI;

    private static AiImageProvider ParseImageProvider(IConfiguration configuration, string key, AiImageProvider fallback)
    {
        var setting = configuration[key];
        return string.IsNullOrWhiteSpace(setting)
            ? fallback
            : Enum.TryParse<AiImageProvider>(setting, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"'{setting}' is not a valid {key}. Valid values: {string.Join(", ", Enum.GetNames<AiImageProvider>())}.");
    }

    /// <summary>The painter a provider value selects, built on demand so its secrets are only required when chosen.</summary>
    private static IImageGenerationService CreatePainter(IServiceProvider sp, AiImageProvider provider) => provider switch
    {
        AiImageProvider.AzureOpenAI => ActivatorUtilities.CreateInstance<AzureOpenAIImageService>(sp),
        AiImageProvider.Local => ActivatorUtilities.CreateInstance<LocalImageService>(sp),
        _ => ActivatorUtilities.CreateInstance<GeminiComicService>(sp)
    };

    /// <summary>
    /// Registers all infrastructure services and Azure clients
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Configure options
        services.Configure<AzureStorageOptions>(
            configuration.GetSection(AzureStorageOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(
            configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<ComicOptions>(
            configuration.GetSection(ComicOptions.SectionName));
        services.Configure<GenerationBudgetOptions>(
            configuration.GetSection(GenerationBudgetOptions.SectionName));
        services.Configure<LeaderboardOptions>(
            configuration.GetSection(LeaderboardOptions.SectionName));
        services.Configure<ModerationOptions>(
            configuration.GetSection(ModerationOptions.SectionName));
        services.Configure<OllamaOptions>(
            configuration.GetSection(OllamaOptions.SectionName));
        services.Configure<AiPricingOptions>(
            configuration.GetSection(AiPricingOptions.SectionName));
        services.Configure<ImageQaOptions>(
            configuration.GetSection(ImageQaOptions.SectionName));
        services.Configure<LocalImageOptions>(
            configuration.GetSection(LocalImageOptions.SectionName));
        services.Configure<RemasterOptions>(
            configuration.GetSection(RemasterOptions.SectionName));
        services.Configure<PredrawOptions>(
            configuration.GetSection(PredrawOptions.SectionName));

        // Chat and image are chosen separately, so an image-model experiment is not also a
        // scorer experiment. Drafts and remasters are chosen separately too: cheap for every
        // comic, premium for the few that get the views.
        var imageProvider = ParseImageProvider(configuration, AiProviderConfigurationKey, DefaultImageProvider);
        var remasterProvider = ParseImageProvider(configuration, AiRemasterProviderConfigurationKey, AiImageProvider.Gemini);

        // A local model's output is not what the product publishes, and a laptop is not a server.
        if ((imageProvider == AiImageProvider.Local || remasterProvider == AiImageProvider.Local) && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{AiImageProvider.Local} is a Development-only image provider; it cannot run in {environment.EnvironmentName}.");
        }

        var chatProviderSetting = configuration[AiChatProviderConfigurationKey];
        var chatProvider = string.IsNullOrWhiteSpace(chatProviderSetting)
            ? AiChatProvider.AzureOpenAI
            : Enum.TryParse<AiChatProvider>(chatProviderSetting, ignoreCase: true, out var parsedChat)
                ? parsedChat
                : throw new InvalidOperationException(
                    $"'{chatProviderSetting}' is not a valid {AiChatProviderConfigurationKey}. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<AiChatProvider>())}.");

        var useAzureChat = chatProvider == AiChatProvider.AzureOpenAI;

        // Storage clients: cloud resolves via System-assigned Managed Identity against the
        // account endpoints (NET_RULES 5.4); connection strings remain only for local Azurite.
        var tableEndpoint = configuration["AzureStorage:TableEndpoint"];
        var blobEndpoint = configuration["AzureStorage:BlobEndpoint"];

        // Storage:UseAzurite short-circuits Key Vault connection strings so local dev can run
        // against the emulator (docker-compose) instead of the real storage account. Without
        // this, Key Vault secrets always win and every local run mutates production data.
        if (configuration.GetValue<bool>("Storage:UseAzurite"))
        {
            const string azurite =
                "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
                "TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;" +
                "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;" +
                "QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";

            services.AddSingleton(_ => new TableServiceClient(azurite));
            services.AddSingleton(_ => new BlobServiceClient(azurite));
        }
        else if (!string.IsNullOrEmpty(tableEndpoint))
        {
            var credential = new DefaultAzureCredential();
            services.AddSingleton(_ => new TableServiceClient(new Uri(tableEndpoint), credential));
            services.AddSingleton(_ => new BlobServiceClient(
                new Uri(blobEndpoint ?? throw new InvalidOperationException(
                    "AzureStorage:BlobEndpoint is required when AzureStorage:TableEndpoint is set.")),
                credential));
        }
        else
        {
            var tableConnectionString = configuration.GetConnectionString("AzureTableStorage")
                ?? configuration["AzureTableStorage"]
                ?? Environment.GetEnvironmentVariable("AZURE_TABLE_STORAGE_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "Set AzureStorage:TableEndpoint (Managed Identity) or ConnectionStrings:AzureTableStorage (Azurite).");

            var blobConnectionString = configuration.GetConnectionString("AzureBlobStorage")
                ?? configuration["AzureBlobStorage"]
                ?? Environment.GetEnvironmentVariable("AZURE_BLOB_STORAGE_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "Set AzureStorage:BlobEndpoint (Managed Identity) or ConnectionStrings:AzureBlobStorage (Azurite).");

            services.AddSingleton(_ => new TableServiceClient(tableConnectionString));
            services.AddSingleton(_ => new BlobServiceClient(blobConnectionString));
        }

        // Register Azure OpenAI client — only when Azure is the active CHAT provider. Under the
        // Ollama chat path the Azure config may be absent, so we must not fail-fast on it.
        if (useAzureChat)
        {
            var openAiOptions = configuration.GetSection(AzureOpenAIOptions.SectionName)
                .Get<AzureOpenAIOptions>()
                ?? throw new InvalidOperationException("AzureOpenAI configuration is required");

            if (string.IsNullOrEmpty(openAiOptions.Endpoint) || string.IsNullOrEmpty(openAiOptions.ApiKey))
            {
                throw new InvalidOperationException("AzureOpenAI Endpoint and ApiKey must be configured");
            }

            services.AddSingleton(_ => new AzureOpenAIClient(
                new Uri(openAiOptions.Endpoint),
                new AzureKeyCredential(openAiOptions.ApiKey)));
        }

        // Provision tables + blob container once at startup (fail-fast) instead of per-request.
        services.AddHostedService<TableStorageInitializer>();

        // Register repositories
        services.AddScoped<RestaurantRepository>();
        services.AddScoped<IComicRepository, ComicRepository>();
        services.AddScoped<ILeaderboardRepository, LeaderboardRepository>();
        services.AddScoped<HallOfFameRepository>();
        // Same instance behind both: the slice reads through the concrete type, Takedowns
        // erases through the Shared contract.
        services.AddScoped<IHallOfFameArchive>(sp => sp.GetRequiredService<HallOfFameRepository>());
        services.AddScoped<ComicReportRepository>();
        services.AddScoped<ShareLinkRepository>();
        services.AddScoped<ModerationRepository>();
        // Same instance behind both, mirroring HallOfFameRepository: the slice reads through the
        // concrete type, Comics/Reports/Takedowns go through the Shared contract.
        services.AddScoped<IContentModerationGate>(sp => sp.GetRequiredService<ModerationRepository>());
        services.AddScoped<ModerationQueueReader>();
        services.AddScoped<LexicalContentSafetyScreener>();
        services.AddScoped<IContentSafetyScreener>(sp =>
            string.IsNullOrWhiteSpace(configuration[$"{ModerationOptions.SectionName}:{nameof(ModerationOptions.ContentSafetyEndpoint)}"])
                ? sp.GetRequiredService<LexicalContentSafetyScreener>()
                : ActivatorUtilities.CreateInstance<AzureContentSafetyScreener>(sp));
        // The screen sits before the paid image call on every generation: a slow classifier is
        // given a few seconds and then the lexical verdict stands.
        services.AddHttpClient(AzureContentSafetyScreener.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(4));
        services.AddSingleton<OwnerAlerts>();
        services.AddHttpClient(OwnerAlerts.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<ModerationTriageService>();
        services.AddScoped<IAnalysisRepository, AnalysisRepository>();
        services.AddModerationAuthorization(configuration);
        services.AddScoped<GenerationBudgetService>();
        services.AddScoped<DiagnosticsSnapshotQueryHandler>();

        // Register services
        services.AddHttpClient<GoogleMapsService>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(5))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.ConnectionClose = false;
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.Delay = TimeSpan.FromSeconds(2);
                options.Retry.UseJitter = true;

                // Trip the circuit after 5 consecutive failures, keep it open for 30s.
                // Prevents Maps outages from cascading into comic 5xx storms.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.MinimumThroughput = 5;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

                // Aggregate per-attempt budget must stay below the 10s user-facing latency target.
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            });
        services.AddScoped<IRestaurantService, RestaurantService>();

        services.AddScoped<IBlobStorageService, BlobStorageService>();

        // Chat provider (strangeness analysis + panel captions). Selected independently of the
        // image provider; see the note above. Ollama is the local, zero-marginal-cost tier.
        if (chatProvider == AiChatProvider.Ollama)
            services.AddScoped<IChatCompletionService, OllamaChatService>();
        else
            services.AddScoped<IChatCompletionService, AzureOpenAIChatService>();

        // Cost accounting for every model call, tagged by provider and model. Registered once so
        // there is a single metric whose value is "what this app spent" rather than one metric per
        // provider on three incomparable scales.
        services.AddSingleton<AiCostTracker>();

        // Single flight per place: stops a double-tap from commissioning the same paid comic
        // twice. Singleton because the gates must be shared across requests, not per request.
        services.AddSingleton<ComicGenerationLock>();

        // Image providers: gpt-image on Azure (AzureOpenAIImageService), Gemini (GeminiComicService)
        // or a local server (LocalImageService), all prompted by ComicImagePrompt. The named
        // HttpClients carry a timeout and NOTHING else — no retry, no hedging. An image POST that
        // is slow or answers 5xx may already have been billed, so re-sending it can pay for the
        // same comic twice. All are registered: the draft and remaster painters can differ.
        foreach (var imageClientName in new[] { "AzureOpenAIImageApi", "GeminiApi" })
        {
            services.AddHttpClient(imageClientName)
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(90));
        }

        services.AddHttpClient(LocalImageService.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(Math.Max(30, sp.GetRequiredService<IOptions<LocalImageOptions>>().Value.TimeoutSeconds)));

        services.AddScoped(sp => CreatePainter(sp, imageProvider));

        // The remaster painter exists only when it is a different painter from the draft one —
        // otherwise there is nothing to upgrade to — and only when its own secrets are present,
        // so a missing Gemini key disables remastering rather than failing startup.
        if (remasterProvider != imageProvider)
        {
            services.AddKeyedScoped<IImageGenerationService>(RemasterOptions.ServiceKey, (sp, _) =>
            {
                try
                {
                    return CreatePainter(sp, remasterProvider);
                }
                catch (InvalidOperationException ex)
                {
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ComicRemasterService))
                        .LogWarning(ex, "Remaster painter {Provider} is not configured; remastering is off", remasterProvider);
                    // Null is a supported factory result: GetKeyedService hands it back, and the
                    // remaster pass treats "no painter" as "nothing to do".
                    return null!;
                }
            });
            services.AddHostedService<ComicRemasterService>();
        }

        services.AddHostedService<ComicPredrawService>();

        // A sampled vision check of drawn comics. It reads the scorer's Azure resource, so it
        // exists only when Azure is the chat provider; the pipeline treats it as optional.
        if (useAzureChat)
        {
            services.AddSingleton<ComicImageQaQueue>();
            services.AddHostedService<ComicImageQaService>();
        }
        services.AddScoped<IComicTextOverlayService, ComicTextOverlayService>();
        services.AddScoped<ShareCardService>();
        services.AddScoped<IComicGenerationService, ComicGenerationService>();
        services.AddScoped<AudioComicService>();
        // One attempt per line, no resilience handler: a re-sent speech request is billed again.
        services.AddHttpClient(AudioComicService.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(45));
        services.AddScoped<ILiveComicLookup, LiveComicLookup>();
        services.AddScoped<ILeaderboardService, LeaderboardService>();

        return services;
    }
}
