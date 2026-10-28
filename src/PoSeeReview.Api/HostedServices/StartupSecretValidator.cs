using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Configuration;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.HostedServices;

/// <summary>
/// Fail-fast validator that runs once at startup.
/// <para>
/// <c>GoogleMaps:ApiKey</c> is required in every environment (Dev, Test, Prod) — the
/// restaurants page is unusable without it, and a missing key means the dev placeholder
/// was left in place or KV didn't supply the value (2026-06-15 incident).
/// </para>
/// <para>
/// The other AI secrets (AzureOpenAI*, Google:GeminiApiKey) follow the PoFunQuiz pattern:
/// warn-loud in Development / Test, throw in Production, so a misconfigured deployment can
/// never silently serve fabricated data.
/// </para>
/// <para>
/// <c>AzureOpenAI:DeploymentName</c> is checked against a known-good set in every
/// environment. In Dev/Test the mismatch is a warning (so local iteration isn't blocked
/// when the upstream team is mid-migration), in Production it throws. This is what
/// caught the 2026-06-15 incident where the app-prefixed mirror in kv-poshared2 was
/// silently set to <c>gpt-4o</c> (a model that does not exist in
/// <c>po-aiservices-shared2</c>).
/// </para>
/// </summary>
public sealed class StartupSecretValidator(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<StartupSecretValidator> logger) : IHostedService
{
    // Static templates — CA2254-safe and zero-allocation per call.
    private static readonly Action<ILogger, string, Exception?> MissingInProd =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(9001, "MissingRequiredSecret"),
            "Required configuration key '{Key}' is missing in Production. Refusing to start.");

    private static readonly Action<ILogger, string, Exception?> MissingInDev =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(9002, "MissingOptionalSecret"),
            "Configuration key '{Key}' is missing. AI/Map features will be unavailable.");

    private static readonly Action<ILogger, string, Exception?> DeploymentDriftWarn =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(9003, "DeploymentDrift"),
            "AzureOpenAI:DeploymentName is '{Deployment}' which is not in the known-good set. " +
            "This will fail in Production. Check both kv-poshared2 secrets: 'AzureOpenAI--DeploymentName' " +
            "(shared) and 'PoSeeReview--AzureOpenAI--DeploymentName' (app-prefixed, wins).");

    private static readonly Action<ILogger, string, Exception?> CapNotAppliedWarn =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(9004, "TokenCapNotApplied"),
            "AzureOpenAI:MaxCompletionTokens is set ({Cap}) but will NOT be applied. The analysis " +
            "call will run uncapped. Clear the setting, or set AzureOpenAI:IsReasoningModel=false " +
            "when the deployment is an instruct model, which is the only case where the cap " +
            "reaches the wire.");

    /// <summary>
    /// Known-good deployment names in <c>po-aiservices-shared2</c> (PoShared RG, East US).
    /// Update this set when a new deployment is provisioned. The validator warns (Dev/Test)
    /// or throws (Production) if the configured value is not in this set.
    /// Verified 2026-06-14 via <c>az cognitiveservices account deployment list</c>:
    /// gpt-5.4-nano / 2026-03-17 / GlobalStandard cap=1 / Running.
    /// </summary>
    private static readonly HashSet<string> KnownGoodDeployments = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.4-nano",   // sole live deployment
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // GoogleMaps:ApiKey is fail-fast in every environment. Reason: the page is unusable
        // when the key is missing (RestaurantsController returns 503 for every nearby search),
        // and silent shadow-by-env-var is the exact failure mode that caused the 2026-06-15
        // "Restaurant search is temporarily unavailable" incident. If the key is missing here,
        // it means KV didn't supply it and the dev placeholder wasn't removed — refuse to boot
        // so the misconfiguration is loud, not silent.
        const string GoogleMapsKey = "GoogleMaps:ApiKey";
        var googleMapsValue = configuration[GoogleMapsKey];
        if (string.IsNullOrWhiteSpace(googleMapsValue))
        {
            throw new InvalidOperationException(
                "StartupSecretValidator: required configuration key 'GoogleMaps:ApiKey' is missing. " +
                "kv-poshared2 must contain a non-empty secret named either 'GoogleMaps--ApiKey' (shared) " +
                "or 'PoSeeReview--GoogleMaps--ApiKey' (app-prefixed). Refusing to start.");
        }

        // AI secrets: warn in Dev/Test, throw in Production (PoFunQuiz pattern).
        //
        // The list follows the providers actually selected. When chat and image were one switch
        // this could not be expressed — pointing the app at a local chat model still demanded
        // Azure credentials that would never be used, which turned "try the cheap tier" into a
        // deploy that refused to start.
        var chatProviderSetting = configuration[InfrastructureServiceCollectionExtensions.AiChatProviderConfigurationKey];
        var imageProviderSetting = configuration[InfrastructureServiceCollectionExtensions.AiProviderConfigurationKey];

        var usesAzureChat = string.IsNullOrWhiteSpace(chatProviderSetting)
            || string.Equals(chatProviderSetting, nameof(AiChatProvider.AzureOpenAI), StringComparison.OrdinalIgnoreCase);

        var imageProvider = string.IsNullOrWhiteSpace(imageProviderSetting)
            ? InfrastructureServiceCollectionExtensions.DefaultImageProvider.ToString()
            : imageProviderSetting;
        var usesGeminiImages = string.Equals(imageProvider, nameof(AiImageProvider.Gemini), StringComparison.OrdinalIgnoreCase);
        var usesAzureImages = string.Equals(imageProvider, nameof(AiImageProvider.AzureOpenAI), StringComparison.OrdinalIgnoreCase);

        var prodRequired = new List<string>(capacity: 4);

        if (usesAzureChat || usesAzureImages)
        {
            prodRequired.Add("AzureOpenAI:Endpoint");
            prodRequired.Add("AzureOpenAI:ApiKey");
        }

        if (usesAzureChat)
        {
            prodRequired.Add("AzureOpenAI:DeploymentName");
        }

        // Only the DRAFT painter's secrets are required. The remaster painter (Gemini by default)
        // is an upgrade: without its key, remastering switches itself off and comics still draw.
        if (usesGeminiImages)
        {
            prodRequired.Add("Google:GeminiApiKey");
        }

        var missing = new List<string>(capacity: prodRequired.Count);
        foreach (var key in prodRequired)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }

        if (missing.Count > 0)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
            {
                foreach (var key in missing)
                {
                    MissingInDev(logger, key, null);
                }
            }
            else
            {
                foreach (var key in missing)
                {
                    MissingInProd(logger, key, null);
                }

                throw new InvalidOperationException(
                    $"StartupSecretValidator: {missing.Count} required configuration value(s) missing in Production: " +
                    string.Join(", ", missing));
            }
        }

        // Deployment-name drift guard. Runs in every environment:
        // - Dev/Test: WARN so a developer mid-migration isn't blocked, but the warning is loud
        //   enough to show up in Serilog at the next deploy.
        // - Production: THROW, because DeploymentNotFound means every comic generation 5xx's.
        //
        // 2026-06-15 incident: the app-prefixed mirror in kv-poshared2 was set to "gpt-4o"
        // (a model that does not exist in po-aiservices-shared2). The previous drift guard
        // only ran in non-Dev and only checked a hard-coded constant, so the bug was silent
        // for ~6 weeks. The new set-based check is strict enough to catch that case.
        //
        // Only meaningful when Azure is the chat provider. A leftover AzureOpenAI:DeploymentName
        // from a previous configuration must not be able to stop a deployment that is running the
        // local tier — the setting is not read on that path, so validating it would be refusing to
        // start over a value nothing consults.
        var configuredDeployment = configuration["AzureOpenAI:DeploymentName"];
        if (usesAzureChat
            && !string.IsNullOrWhiteSpace(configuredDeployment)
            && !KnownGoodDeployments.Contains(configuredDeployment))
        {
            var knownGood = "{" + string.Join(", ", KnownGoodDeployments) + "}";
            if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
            {
                DeploymentDriftWarn(logger, $"{configuredDeployment} (not in {knownGood})", null);
            }
            else
            {
                throw new InvalidOperationException(
                    $"StartupSecretValidator: AzureOpenAI:DeploymentName is '{configuredDeployment}', " +
                    $"which is not in the known-good set {knownGood} for po-aiservices-shared2. " +
                    "Update BOTH kv-poshared2 secrets: 'AzureOpenAI--DeploymentName' (shared) and " +
                    "'PoSeeReview--AzureOpenAI--DeploymentName' (app-prefixed — this one wins). " +
                    "If the value is correct, add it to KnownGoodDeployments in StartupSecretValidator.cs " +
                    "and update the comment with the verification command output.");
            }
        }

        // A completion cap that cannot reach the wire. This is the same class of problem as the
        // drift guard above — configuration that reads as active and is not — and it is a warning
        // rather than a throw because the app works perfectly without it; the cost is a missing
        // ceiling, not a broken comic. Silently ignoring it is what must not happen: the setting
        // exists to bound spend, and a spend bound that does nothing is worse than none at all.
        //
        // Only a cap that is actually CONFIGURED can fail to be applied. An unset cap is the
        // normal, supported state — the first version of this check warned on every single startup
        // with "( configured against a reasoning deployment)" and no number in it, because it
        // tested "would the cap apply" rather than "was a cap asked for".
        var configuredCap = configuration.GetValue<int?>("AzureOpenAI:MaxCompletionTokens");
        var isReasoningModel = configuration.GetValue("AzureOpenAI:IsReasoningModel", true);

        if (usesAzureChat && configuredCap is > 0 && !ChatTokenBudget.CanApplyCap(configuredCap, isReasoningModel))
        {
            CapNotAppliedWarn(logger, configuredCap.Value.ToString(CultureInfo.InvariantCulture), null);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

