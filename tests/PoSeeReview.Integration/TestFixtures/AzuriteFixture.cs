using Azure.Data.Tables;
using Testcontainers.Azurite;
using Xunit;

namespace PoSeeReview.Integration.TestFixtures;

/// <summary>
/// Test fixture that starts a dedicated Azurite container via Testcontainers for each test run.
/// The container is spun up automatically — no pre-running Docker/Azurite process required.
/// Implements IAsyncLifetime so xUnit calls InitializeAsync/DisposeAsync.
/// </summary>
public class AzuriteFixture : IAsyncLifetime
{
    // --skipApiVersionCheck: the Azure SDK's storage API version can be newer than
    // the Azurite release; the emulator still supports the operations we use.
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public TableServiceClient TableServiceClient { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;

    // Always available once InitializeAsync completes — no skip logic needed
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Public fixture instance member")]
    public bool IsAzuriteAvailable => true;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        TableServiceClient = new TableServiceClient(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Kept for backwards compatibility with existing test usages — always a no-op now
    /// because the container is guaranteed to be running after InitializeAsync.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Public fixture instance member")]
    public void EnsureAzuriteAvailable() { /* Testcontainers guarantees availability */ }

    /// <summary>
    /// Creates a test table and returns the client.
    /// </summary>
    public async Task<TableClient> CreateTestTableAsync(string tableName)
    {
        var tableClient = TableServiceClient.GetTableClient(tableName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tableClient.CreateIfNotExistsAsync(cts.Token);
        return tableClient;
    }

    /// <summary>
    /// Deletes a test table for cleanup.
    /// </summary>
    public async Task DeleteTestTableAsync(string tableName)
    {
        var tableClient = TableServiceClient.GetTableClient(tableName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tableClient.DeleteAsync(cts.Token);
    }

    /// <summary>
    /// Clears all entities from a table, creating it first if it does not yet exist.
    /// </summary>
    public async Task ClearTableAsync(string tableName)
    {
        var tableClient = TableServiceClient.GetTableClient(tableName);
        await tableClient.CreateIfNotExistsAsync();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>())
        {
            await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
        }
    }
}
