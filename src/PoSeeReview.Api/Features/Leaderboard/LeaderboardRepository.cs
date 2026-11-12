using Azure.Data.Tables;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Leaderboard;

/// <summary>
/// Repository for leaderboard persistence with inverted RowKey for descending sort
/// Table Name: PoSeeReviewLeaderboard
/// PartitionKey: LEADERBOARD_{Region}
/// RowKey: {InvertedScore}_{PlaceId}
/// </summary>
public class LeaderboardRepository : ILeaderboardRepository
{
    private readonly TableClient _tableClient;
    private readonly ILogger<LeaderboardRepository> _logger;

    public LeaderboardRepository(
        TableServiceClient tableServiceClient,
        IOptions<AzureStorageOptions> options,
        ILogger<LeaderboardRepository> logger)
    {
        _logger = logger;

        var tableName = options.Value.LeaderboardTableName;
        _tableClient = tableServiceClient.GetTableClient(tableName);
    }

    /// <summary>
    /// Gets top N entries for a region, sorted by strangeness score descending
    /// Leverages inverted RowKey for efficient sorting
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetTopEntriesAsync(RegionCode region, int limit)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Region cannot be empty", nameof(region));

        if (limit < 1 || limit > 50)
            throw new ArgumentException("Limit must be between 1 and 50", nameof(limit));

        var partitionKey = LeaderboardEntity.PartitionKeyFor(region);

        _logger.LogInformation("Fetching top {Limit} entries for region {Region}", limit, region);

        try
        {
            var filter = TableClient.CreateQueryFilter<LeaderboardEntity>(
                e => e.PartitionKey == partitionKey);

            var query = _tableClient.QueryAsync<LeaderboardEntity>(
                filter: filter,
                maxPerPage: limit
            );

            var entries = new List<LeaderboardEntry>();
            var rank = 1;

            await foreach (var entity in query)
            {
                entries.Add(entity.ToDomain(rank++));

                if (entries.Count >= limit)
                    break;
            }

            _logger.LogInformation("Retrieved {Count} entries for region {Region}", entries.Count, region);
            return entries;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("No leaderboard entries found for region {Region}", region);
            return new List<LeaderboardEntry>();
        }
    }

    /// <summary>Rows a global read will scan before giving up on ordering the rest.</summary>
    private const int MaxGlobalScanRows = 500;

    /// <inheritdoc />
    public async Task<List<LeaderboardEntry>> GetTopEntriesGlobalAsync(int limit)
    {
        if (limit < 1 || limit > 50)
            throw new ArgumentException("Limit must be between 1 and 50", nameof(limit));

        _logger.LogInformation("Fetching top {Limit} entries across all regions", limit);

        try
        {
            // No partition filter: the table holds only leaderboard rows. The inverted RowKey
            // sorts within a partition, not across them, so the global order is assembled here.
            var query = _tableClient.QueryAsync<LeaderboardEntity>(maxPerPage: 100);

            var entries = new List<LeaderboardEntry>();

            await foreach (var entity in query)
            {
                entries.Add(entity.ToDomain(0));

                if (entries.Count >= MaxGlobalScanRows)
                {
                    break;
                }
            }

            var top = entries
                .OrderByDescending(e => e.StrangenessScore)
                .Take(limit)
                .ToList();

            for (var i = 0; i < top.Count; i++)
            {
                top[i].Rank = i + 1;
            }

            _logger.LogInformation("Retrieved {Count} global leaderboard entries from {Scanned} rows", top.Count, entries.Count);
            return top;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("No leaderboard entries found for the global board");
            return new List<LeaderboardEntry>();
        }
    }

    /// <summary>
    /// Gets a specific entry by placeId and region
    /// </summary>
    public async Task<LeaderboardEntry?> GetByPlaceIdAsync(PlaceId placeId, RegionCode region)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId cannot be empty", nameof(placeId));

        if (region.IsEmpty)
            throw new ArgumentException("Region cannot be empty", nameof(region));

        var placeKey = placeId.Value;

        var partitionKey = LeaderboardEntity.PartitionKeyFor(region);

        try
        {
            // Query by PlaceId property (secondary filter)
            var filter = TableClient.CreateQueryFilter<LeaderboardEntity>(
                e => e.PartitionKey == partitionKey && e.PlaceId == placeKey);

            var query = _tableClient.QueryAsync<LeaderboardEntity>(filter: filter);

            await foreach (var entity in query)
            {
                return entity.ToDomain(0); // Rank will be recalculated when needed
            }

            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<LeaderboardEntry?> GetByPlaceIdAsync(PlaceId placeId)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId cannot be empty", nameof(placeId));

        var placeKey = placeId.Value;

        try
        {
            // Cross-region scan — same pattern as DeleteByPlaceIdAsync
            var filter = TableClient.CreateQueryFilter<LeaderboardEntity>(
                e => e.PlaceId == placeKey);

            var query = _tableClient.QueryAsync<LeaderboardEntity>(filter: filter);

            await foreach (var entity in query)
            {
                return entity.ToDomain(0);
            }

            return null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Upserts a leaderboard entry
    /// If entry exists with different score, old entry is deleted and new one created
    /// (RowKey includes score, so score changes require delete+insert)
    /// </summary>
    public async Task UpsertAsync(LeaderboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.PlaceId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(entry));

        if (entry.Region.IsEmpty)
            throw new ArgumentException("Region is required", nameof(entry));

        var placeKey = entry.PlaceId.Value;

        var partitionKey = LeaderboardEntity.PartitionKeyFor(entry.Region);

        // Find the existing row's RowKey (RowKey embeds the score, so a score change means a
        // different RowKey). We capture it before writing so we can prune the stale row afterwards.
        string? oldRowKey = null;
        var existingFilter = TableClient.CreateQueryFilter<LeaderboardEntity>(
            e => e.PartitionKey == partitionKey && e.PlaceId == placeKey);

        await foreach (var existing in _tableClient.QueryAsync<LeaderboardEntity>(filter: existingFilter))
        {
            oldRowKey = existing.RowKey;
            break;
        }

        var entity = LeaderboardEntity.FromDomain(entry);

        // Rewrite-then-delete: write the new row FIRST so a crash mid-operation can never leave
        // the entry missing. Only after the new row is durable do we remove the stale one.
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);

        if (oldRowKey != null && oldRowKey != entity.RowKey)
        {
            try
            {
                await _tableClient.DeleteEntityAsync(partitionKey, oldRowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Stale row already gone — nothing to prune.
            }
        }

        _logger.LogInformation(
            "Upserted leaderboard entry for {PlaceId} in {Region} with score {Score}",
            entry.PlaceId, entry.Region, entry.StrangenessScore);
    }

    /// <summary>
    /// Deletes a leaderboard entry
    /// Must query first to find the RowKey (which includes score)
    /// </summary>
    public async Task DeleteAsync(PlaceId placeId, RegionCode region)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId cannot be empty", nameof(placeId));

        if (region.IsEmpty)
            throw new ArgumentException("Region cannot be empty", nameof(region));

        var placeKey = placeId.Value;

        var partitionKey = LeaderboardEntity.PartitionKeyFor(region);

        try
        {
            // Find the entity by PlaceId to get its RowKey
            var filter = TableClient.CreateQueryFilter<LeaderboardEntity>(
                e => e.PartitionKey == partitionKey && e.PlaceId == placeKey);

            var query = _tableClient.QueryAsync<LeaderboardEntity>(filter: filter);

            await foreach (var entity in query)
            {
                await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                _logger.LogInformation("Deleted leaderboard entry {PlaceId} from {Region}", placeId, region);
                return;
            }

            _logger.LogDebug("Leaderboard entry {PlaceId} not found in {Region}", placeId, region);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already deleted, ignore
            _logger.LogDebug("Entry {PlaceId} already deleted from {Region}", placeId, region);
        }
    }
}
