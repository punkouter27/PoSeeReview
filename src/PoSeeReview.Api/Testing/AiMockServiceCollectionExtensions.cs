using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using PoSeeReview.Api.Features.Comics;

namespace PoSeeReview.Api.Testing;

/// <summary>
/// Wires <see cref="AiMockDelegatingHandler"/> into the BFF so AI provider calls are
/// intercepted in test environments. Call this from a test WebApplicationFactory's
/// service configuration — never in Production.
/// </summary>
public static class AiMockServiceCollectionExtensions
{
    public static IServiceCollection AddMockedAiBoundaries(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Surface the active mock boundary on /api/diag/mock-status (NET_RULES 6.5).
        services.AddSingleton<IMockable, AiMockDelegatingHandler>();

        // ── Gemini / Imagen: HttpClientFactory client → message-handler boundary ──
        // The factory chains the InnerHandler automatically for AddHttpMessageHandler.
        services.AddHttpClient("GeminiApi")
            .AddHttpMessageHandler(() => new AiMockDelegatingHandler());
        services.AddHttpClient("AzureOpenAIImageApi")
            .AddHttpMessageHandler(() => new AiMockDelegatingHandler());

        // ── Azure AI Foundry / Azure OpenAI: SDK uses the Azure.Core pipeline, so the
        //    DelegatingHandler is injected via a custom HttpClient transport. ──
        var openAiOptions = configuration.GetSection(AzureOpenAIOptions.SectionName)
            .Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();

        var endpoint = string.IsNullOrWhiteSpace(openAiOptions.Endpoint)
            ? "https://test.openai.azure.com/"
            : openAiOptions.Endpoint;
        var apiKey = string.IsNullOrWhiteSpace(openAiOptions.ApiKey)
            ? "test-key-00000000000000000000AB"
            : openAiOptions.ApiKey;

        var mockHttpClient = new HttpClient(new AiMockDelegatingHandler
        {
            InnerHandler = new HttpClientHandler(),
        });

        services.RemoveAll<AzureOpenAIClient>();
        services.AddSingleton(_ =>
        {
            var clientOptions = new AzureOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(mockHttpClient),
            };
            return new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), clientOptions);
        });

        return services;
    }
}
