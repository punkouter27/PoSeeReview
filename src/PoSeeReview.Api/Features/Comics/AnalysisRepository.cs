using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// One place's cached analysis: the scorer's verdict on a specific set of reviews under a specific
/// prompt, plus how often people have looked for the place since.
/// </summary>
public sealed record CachedAnalysis(
    PlaceId PlaceId,
    string RestaurantName,
    string ReviewsHash,
    int PromptVersion,
    StrangenessAnalysis Analysis,
    ComicSource[] Sources,
    int Demand);

/// <summary>The analysis cache, behind an interface so the pipeline's unit tests can run without it.</summary>
public interface IAnalysisRepository
{
    Task<CachedAnalysis?> GetAsync(PlaceId placeId, CancellationToken cancellationToken = default);

    Task SaveAsync(CachedAnalysis analysis, CancellationToken cancellationToken = default);

    /// <summary>Counts one sighting of a place someone looked for. Approximate by design.</summary>
    Task RecordDemandAsync(PlaceId placeId, CancellationToken cancellationToken = default);

    /// <summary>Zeroes the count after a pre-draw attempt, so a place must be wanted again to be tried again.</summary>
    Task ResetDemandAsync(PlaceId placeId, CancellationToken cancellationToken = default);

    /// <summary>Analyses under <paramref name="promptVersion"/> scoring and wanted enough to pre-draw.</summary>
    Task<List<CachedAnalysis>> GetPredrawCandidatesAsync(int promptVersion, int minScore, int minDemand, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Table Storage for <see cref="CachedAnalysis"/>, one row per place.
/// <para>
/// <b>Why the score is cached apart from the comic.</b> A comic is an image plus the analysis it
/// was drawn from, and only the image is random in a way anyone wants. Re-asking the scorer on
/// every redraw re-rolled the score too, so a Regenerate could turn a 40 into a 12 and drop a
/// place off the board without a single review changing. Keyed by a hash of the reviews and the
/// prompt version, the verdict changes exactly when its inputs do.
/// </para>
/// </summary>
public sealed class AnalysisRepository(
    TableServiceClient tableServiceClient,
    IOptions<AzureStorageOptions> options,
    TimeProvider timeProvider) : IAnalysisRepository
{
    private const string Partition = "ANALYSIS";

    private readonly TableClient _table = tableServiceClient.GetTableClient(options.Value.AnalysesTableName);

    public async Task<CachedAnalysis?> GetAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        var row = await TryGetAsync(placeId, cancellationToken);
        return row?.ToDomain();
    }

    public Task SaveAsync(CachedAnalysis analysis, CancellationToken cancellationToken = default) =>
        // Merge, and Demand left null: a re-analysis must not wipe the count of people who wanted it.
        _table.UpsertEntityAsync(AnalysisEntity.FromDomain(analysis, timeProvider.GetUtcNow()), TableUpdateMode.Merge, cancellationToken);

    public async Task RecordDemandAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        // Read-then-merge without an ETag: two simultaneous sightings may count once. The number
        // only ranks pre-draw candidates, and a lost increment is cheaper than a retry loop.
        var row = await TryGetAsync(placeId, cancellationToken);
        if (row is null)
        {
            return;
        }

        await _table.UpsertEntityAsync(new TableEntity(Partition, row.RowKey)
        {
            [nameof(AnalysisEntity.Demand)] = (row.Demand ?? 0) + 1,
            [nameof(AnalysisEntity.LastDemandAt)] = timeProvider.GetUtcNow()
        }, TableUpdateMode.Merge, cancellationToken);
    }

    public Task ResetDemandAsync(PlaceId placeId, CancellationToken cancellationToken = default) =>
        _table.UpsertEntityAsync(new TableEntity(Partition, placeId.Value)
        {
            [nameof(AnalysisEntity.Demand)] = 0
        }, TableUpdateMode.Merge, cancellationToken);

    public async Task<List<CachedAnalysis>> GetPredrawCandidatesAsync(
        int promptVersion, int minScore, int minDemand, int limit, CancellationToken cancellationToken = default)
    {
        var results = new List<CachedAnalysis>();
        var query = _table.QueryAsync<AnalysisEntity>(
            e => e.PartitionKey == Partition && e.PromptVersion == promptVersion && e.Score >= minScore && e.Demand >= minDemand,
            cancellationToken: cancellationToken);

        await foreach (var row in query)
        {
            results.Add(row.ToDomain());
        }

        return [.. results.OrderByDescending(r => r.Demand).ThenByDescending(r => r.Analysis.StrangenessScore).Take(limit)];
    }

    private async Task<AnalysisEntity?> TryGetAsync(PlaceId placeId, CancellationToken cancellationToken)
    {
        try
        {
            return await _table.GetEntityAsync<AnalysisEntity>(Partition, placeId.Value, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    internal sealed class AnalysisEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = Partition;
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string RestaurantName { get; set; } = string.Empty;
        public string ReviewsHash { get; set; } = string.Empty;
        public int PromptVersion { get; set; }
        public int Score { get; set; }
        public int Absurdity { get; set; }
        public int Specificity { get; set; }
        public int StoryPotential { get; set; }
        public int Horror { get; set; }
        public int PanelCount { get; set; }
        public string Narrative { get; set; } = string.Empty;
        public string CaptionsJson { get; set; } = string.Empty;
        public string ScenesJson { get; set; } = string.Empty;
        public string SourcesJson { get; set; } = string.Empty;
        public DateTimeOffset AnalyzedAt { get; set; }

        /// <summary>Null on write so a merge keeps the stored count; see <see cref="SaveAsync"/>.</summary>
        public int? Demand { get; set; }
        public DateTimeOffset? LastDemandAt { get; set; }

        public static AnalysisEntity FromDomain(CachedAnalysis cached, DateTimeOffset now)
        {
            var a = cached.Analysis;
            return new AnalysisEntity
            {
                RowKey = cached.PlaceId.Value,
                RestaurantName = cached.RestaurantName,
                ReviewsHash = cached.ReviewsHash,
                PromptVersion = cached.PromptVersion,
                Score = a.StrangenessScore,
                Absurdity = a.Ratings?.Absurdity ?? 0,
                Specificity = a.Ratings?.Specificity ?? 0,
                StoryPotential = a.Ratings?.StoryPotential ?? 0,
                Horror = a.Ratings?.Horror ?? 0,
                PanelCount = a.PanelCount,
                Narrative = a.Narrative,
                CaptionsJson = JsonSerializer.Serialize(a.Captions ?? []),
                ScenesJson = JsonSerializer.Serialize(a.Scenes ?? []),
                SourcesJson = JsonSerializer.Serialize(cached.Sources),
                AnalyzedAt = now
            };
        }

        public CachedAnalysis ToDomain()
        {
            var sources = Read<ComicSource[]>(SourcesJson) ?? [];
            var analysis = new StrangenessAnalysis(
                Score,
                Math.Clamp(PanelCount, 1, 2),
                Narrative,
                Read<string[]>(CaptionsJson),
                Read<string[]>(ScenesJson),
                new StrangenessRatings(Absurdity, Specificity, StoryPotential, Horror),
                [.. Enumerable.Range(1, Math.Clamp(PanelCount, 1, 2))
                    .Select(panel => (IReadOnlyList<int>)[.. sources.Where(s => s.Panel == panel).Select(s => s.ReviewNumber)])]);

            return new CachedAnalysis(
                PlaceId.From(RowKey), RestaurantName, ReviewsHash, PromptVersion, analysis, sources, Demand ?? 0);
        }

        private static T? Read<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
