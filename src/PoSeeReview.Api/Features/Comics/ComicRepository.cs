using System.Collections.Generic;
using System.Threading;
using Azure.Data.Tables;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Repository for managing comic entities in Azure Table Storage with 24-hour cache TTL.
/// </summary>
public class ComicRepository : IComicRepository
{
    private readonly TableClient _tableClient;
    private readonly ILogger<ComicRepository> _logger;

    public ComicRepository(
        TableServiceClient tableServiceClient,
        IOptions<AzureStorageOptions> options,
        ILogger<ComicRepository> logger)
    {
        var tableName = options.Value.ComicsTableName;
        _tableClient = tableServiceClient.GetTableClient(tableName);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Retrieves a comic by place ID. Returns expired comics (caller must check ExpiresAt).
    /// </summary>
    /// <param name="placeId">Google Maps place ID</param>
    /// <returns>Comic entity if found, null otherwise</returns>
    public async Task<Comic?> GetByPlaceIdAsync(PlaceId placeId)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(placeId));

        try
        {
            var response = await _tableClient.GetEntityAsync<ComicEntity>(
                partitionKey: ComicEntity.PartitionKeyValue,
                rowKey: placeId.Value
            );

            return response.Value.ToDomain();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Inserts or updates a comic entity. Sets 24-hour expiration from current time.
    /// </summary>
    /// <param name="comic">Comic entity to persist</param>
    /// <exception cref="ArgumentNullException">If comic or required fields are null</exception>
    public async Task UpsertAsync(Comic comic)
    {
        ArgumentNullException.ThrowIfNull(comic);

        if (comic.PlaceId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(comic));

        var entity = ComicEntity.FromDomain(comic);
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    /// <summary>
    /// Deletes a comic by place ID.
    /// </summary>
    /// <param name="placeId">Google Maps place ID</param>
    public async Task DeleteAsync(PlaceId placeId)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(placeId));

        try
        {
            await _tableClient.DeleteEntityAsync(
                partitionKey: ComicEntity.PartitionKeyValue,
                rowKey: placeId.Value
            );
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted, ignore
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Comic>> GetExpiredComicsAsync(
        DateTimeOffset cutoff,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be greater than zero");
        }

        var expiredComics = new List<Comic>(capacity: Math.Min(maxResults, 100));

        var filter = TableClient.CreateQueryFilter<ComicEntity>(entity =>
            entity.PartitionKey == ComicEntity.PartitionKeyValue && entity.ExpiresAt < cutoff);

        var query = _tableClient.QueryAsync<ComicEntity>(
            filter: filter,
            maxPerPage: Math.Min(maxResults, 100),
            cancellationToken: cancellationToken);

        await foreach (var entity in query.WithCancellation(cancellationToken))
        {
            expiredComics.Add(entity.ToDomain());

            if (expiredComics.Count >= maxResults)
            {
                break;
            }
        }

        return expiredComics;
    }
}
