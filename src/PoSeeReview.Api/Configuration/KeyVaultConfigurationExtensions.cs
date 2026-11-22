using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Serilog;

namespace PoSeeReview.Api.Configuration;

internal static class KeyVaultConfigurationExtensions
{
    internal static void ConfigureAzureKeyVault(this WebApplicationBuilder builder, bool isTestMode)
    {
        if (isTestMode)
        {
            return;
        }

        try
        {
            // Key Vault URL from environment variable or configuration
            var keyVaultUrl = builder.Configuration["KeyVault:Uri"]
                ?? Environment.GetEnvironmentVariable("KeyVault__Uri");

            if (string.IsNullOrEmpty(keyVaultUrl))
            {
                Log.Warning("KeyVault:Endpoint not configured. Secrets must be provided via environment variables.");
                return;
            }

            Log.Information("Connecting to Azure Key Vault: {KeyVaultUrl}", keyVaultUrl);

            // When running locally, skip the managed-identity IMDS probe to avoid a
            // ~12-second timeout before falling through to AzureCliCredential.
            var isRunningInAzure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));

            var credentialOptions = new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = !isRunningInAzure,
            };

            var credential = new DefaultAzureCredential(credentialOptions);
            var secretClient = new SecretClient(new Uri(keyVaultUrl), credential);

            // Two-pass loading so app-specific secrets always override shared ones.
            builder.Configuration.AddAzureKeyVault(secretClient, new SharedKeyVaultSecretManager());
            Log.Information("Key Vault: shared secrets registered");

            builder.Configuration.AddAzureKeyVault(secretClient, new PrefixKeyVaultSecretManager());
            // /diag lists one secret name through this same client: a live check, not a config read.
            builder.Services.AddSingleton(new PoSeeReview.Api.Features.Diagnostics.KeyVaultSource(secretClient));
            Log.Information("Key Vault: PoSeeReview app-specific secrets registered");
        }
        catch (Exception ex)
        {
            // Keep startup resilient if Key Vault is unavailable.
            Log.Warning(ex, "Failed to configure Azure Key Vault. Falling back to environment variables and app settings.");
        }
    }
}
