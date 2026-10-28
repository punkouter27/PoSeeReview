using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace PoSeeReview.Api.Configuration;

/// <summary>
/// Loads only PoSeeReview-specific secrets from the shared Key Vault (kv-poshared2).
/// Registered AFTER SharedKeyVaultSecretManager so app-specific secrets always override shared ones.
/// Secret "PoSeeReview--GoogleMaps--ApiKey" → config key "GoogleMaps:ApiKey".
/// </summary>
public sealed class PrefixKeyVaultSecretManager : KeyVaultSecretManager
{
    private const string AppPrefix = "PoSeeReview--";

    /// <summary>
    /// Only loads secrets that start with the "PoSeeReview--" prefix.
    /// Shared secrets are handled by SharedKeyVaultSecretManager (lower-priority pass).
    /// </summary>
    public override bool Load(SecretProperties secret) =>
        secret.Name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the "PoSeeReview--" prefix and converts "--" to ":" for config key hierarchy.
    /// Example: "PoSeeReview--GoogleMaps--ApiKey" → "GoogleMaps:ApiKey"
    /// </summary>
    public override string GetKey(KeyVaultSecret secret) =>
        secret.Name[AppPrefix.Length..].Replace("--", ":");
}
