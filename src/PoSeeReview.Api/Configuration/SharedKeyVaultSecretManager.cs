using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace PoSeeReview.Api.Configuration;

/// <summary>
/// Loads shared secrets from the Key Vault (kv-poshared2) — those NOT prefixed with any Po-app name.
/// Registered BEFORE PrefixKeyVaultSecretManager so app-specific secrets always take priority.
/// Example shared secrets: "AzureOpenAI--ApiKey", "ConnectionStrings--AzureTableStorage".
/// </summary>
public sealed class SharedKeyVaultSecretManager : KeyVaultSecretManager
{
    /// <summary>
    /// Allowlist of shared-secret roots (the segment before the first "--") that PoSeeReview
    /// actually consumes. Least privilege: unrelated shared secrets in kv-poshared2
    /// (AzureFace, AzureSpeech, AzureMaps, AzureAI, GoogleVision, ExternalAuth, AzureAd, …)
    /// are never loaded into this app's configuration, shrinking the blast radius and keeping
    /// the diagnostics snapshot to keys this app legitimately uses.
    /// </summary>
    private static readonly HashSet<string> AllowedSharedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "AzureOpenAI",
        "ConnectionStrings",
        "ApplicationInsights",
        "GoogleMaps",
        "Google",
        "KeyVault",
    };

    /// <summary>
    /// Loads a shared secret only when it is NOT a Po-app secret AND its root is on the allowlist.
    /// App-specific secrets ("PoSeeReview--…") are handled by the higher-priority PrefixKeyVaultSecretManager.
    /// </summary>
    public override bool Load(SecretProperties secret)
    {
        var name = secret.Name;

        // Skip any Po-app prefixed secrets (e.g., PoSeeReview--, PoCoupleQuiz--, PoTraffic--)
        if (name.StartsWith("Po", StringComparison.OrdinalIgnoreCase))
        {
            int dashIndex = name.IndexOf("--", StringComparison.Ordinal);
            if (dashIndex > 2) // "Po" + at least one char + "--"
                return false; // belongs to a specific app — skip it
        }

        // Only load shared secrets whose root segment is on the allowlist.
        var separatorIndex = name.IndexOf("--", StringComparison.Ordinal);
        var root = separatorIndex > 0 ? name[..separatorIndex] : name;
        return AllowedSharedRoots.Contains(root);
    }

    /// <summary>
    /// Converts secret name to config key by replacing "--" with ":".
    /// Example: "AzureOpenAI--ApiKey" → "AzureOpenAI:ApiKey"
    /// </summary>
    public override string GetKey(KeyVaultSecret secret) =>
        secret.Name.Replace("--", ":");
}
