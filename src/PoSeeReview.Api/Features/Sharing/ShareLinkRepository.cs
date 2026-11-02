using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Sharing;

/// <summary>
/// Persistence for short links. Owned by the Sharing slice (NET_RULES 2.2).
/// </summary>
public sealed class ShareLinkRepository(
    TableServiceClient tableServiceClient,
    IOptions<AzureStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<ShareLinkRepository> logger)
{
    /// <summary>
    /// Attempts before giving up on minting. Each attempt is one point-read collision check
    /// against a 53^7 space, so reaching five means something other than bad luck.
    /// </summary>
    private const int MaxMintAttempts = 5;

    private readonly TableClient _table =
        tableServiceClient.GetTableClient(options.Value.ShareLinksTableName);

    /// <summary>
    /// Returns the place a code points at, or null when the code is unknown.
    /// <para>
    /// Malformed codes are rejected without a storage round trip — this endpoint is public and
    /// unauthenticated, so a scanner must not be able to turn a wrong guess into a table read.
    /// </para>
    /// </summary>
    public async Task<PlaceId?> ResolveAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!ShareLinkKeys.IsWellFormed(code))
        {
            return null;
        }

        var row = await TryGetAsync<ShareCodeEntity>(ShareLinkKeys.CodePartition, code, cancellationToken);

        return row is null || string.IsNullOrWhiteSpace(row.PlaceId)
            ? null
            : PlaceId.From(row.PlaceId);
    }

    /// <summary>
    /// Returns this place's short code, minting one on first use.
    /// <para>
    /// Idempotent by design: tapping Share twice must not create a second link, or the same
    /// comic ends up with a trail of addresses and no single one to count.
    /// </para>
    /// </summary>
    public async Task<string> GetOrCreateCodeAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        var placeRowKey = ShareLinkKeys.RowKeyFor(placeId);

        var existing = await TryGetAsync<SharePlaceEntity>(ShareLinkKeys.PlacePartition, placeRowKey, cancellationToken);
        if (existing is not null && ShareLinkKeys.IsWellFormed(existing.Code))
        {
            return existing.Code;
        }

        var now = timeProvider.GetUtcNow();

        for (var attempt = 0; attempt < MaxMintAttempts; attempt++)
        {
            var code = ShareLinkKeys.NewCode();

            try
            {
                // AddEntity, not Upsert: the 409 IS the collision check. A read-then-write would
                // let two simultaneous minters hand the same code to two different comics.
                await _table.AddEntityAsync(new ShareCodeEntity
                {
                    PartitionKey = ShareLinkKeys.CodePartition,
                    RowKey = code,
                    PlaceId = placeId.Value,
                    CreatedAt = now
                }, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                logger.LogInformation("Short code collision on attempt {Attempt}; minting another", attempt + 1);
                continue;
            }

            // Written second. If this fails the forward row is an orphan that still resolves
            // correctly, and the next Share mints a fresh code — a wasted row, never a wrong
            // redirect. The reverse order would leave a place pointing at a code that resolves
            // to nothing.
            await _table.UpsertEntityAsync(new SharePlaceEntity
            {
                PartitionKey = ShareLinkKeys.PlacePartition,
                RowKey = placeRowKey,
                Code = code,
                CreatedAt = now
            }, TableUpdateMode.Replace, cancellationToken);

            return code;
        }

        throw new InvalidOperationException(
            $"Could not mint a unique short code after {MaxMintAttempts} attempts.");
    }

    private async Task<T?> TryGetAsync<T>(string partitionKey, string rowKey, CancellationToken cancellationToken)
        where T : class, ITableEntity
    {
        try
        {
            return await _table.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
