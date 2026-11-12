using Azure.Data.Tables;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Integration.TestFixtures;
using Xunit;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Integration.Infrastructure;

/// <summary>
/// Integration tests for RestaurantEntity CRUD operations with Azurite
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Storage")]
[Trait("Suite", "CriticalPath")]
public class TableStorageTests : IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _fixture;
    private const string TestTableName = "PoSeeReviewRestaurants";

    public TableStorageTests(AzuriteFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ModerationRepository_AutomatedWrites_NeverUndoASuppress()
    {
        // Reports and the content screen write under the row's ETag. They used to replace the
        // row from a stale read, so one landing after a moderator's Suppress could clear it.
        var options = Microsoft.Extensions.Options.Options.Create(new PoSeeReview.Api.Storage.AzureStorageOptions());
        await _fixture.ClearTableAsync(options.Value.ModerationTableName);

        var repository = new PoSeeReview.Api.Features.Moderation.ModerationRepository(
            _fixture.TableServiceClient,
            options,
            Microsoft.Extensions.Options.Options.Create(new PoSeeReview.Api.Features.Moderation.ModerationOptions()),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PoSeeReview.Api.Features.Moderation.ModerationRepository>.Instance);
        var placeId = PlaceId.From("ChIJmoderation1");

        await repository.FlagForReviewAsync(placeId, "allegation");          // insert
        Assert.False(await repository.RecordReportAsync(placeId, 1));        // conditional update
        await repository.SuppressAsync(placeId, "takedown");
        await repository.RecordReportAsync(placeId, 5);
        await repository.FlagForReviewAsync(placeId, "allegation");

        var verdict = await repository.EvaluateAsync(placeId);
        Assert.True(verdict.IsSuppressed);
        Assert.Equal("takedown", verdict.Reason);
    }

    [Fact]
    public async Task AnalysisRepository_ReanalysisKeepsDemand_AndPredrawQueryHonoursTheThresholds()
    {
        // Two storage behaviours the analysis cache leans on: a merge with Demand left null keeps
        // the stored count, and the candidate query filters on a column some rows do not have.
        var options = Microsoft.Extensions.Options.Options.Create(new PoSeeReview.Api.Storage.AzureStorageOptions());
        await _fixture.CreateTestTableAsync(options.Value.AnalysesTableName);
        await _fixture.ClearTableAsync(options.Value.AnalysesTableName);
        var repository = new PoSeeReview.Api.Features.Comics.AnalysisRepository(_fixture.TableServiceClient, options, TimeProvider.System);

        PoSeeReview.Api.Features.Comics.CachedAnalysis Analysis(string id, int score) => new(
            PlaceId.From(id), id, "hash", 4,
            new PoSeeReview.Api.Features.Comics.StrangenessAnalysis(score, 1, "n", ["c"], ["s"], new(5, 5, 5), [[1]]),
            [new ComicSource(1, 1, 2)], 0);

        await repository.SaveAsync(Analysis("ChIJwanted", 70));
        await repository.SaveAsync(Analysis("ChIJordinary", 10));
        await repository.SaveAsync(Analysis("ChIJunseen", 80));
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await repository.RecordDemandAsync(PlaceId.From("ChIJwanted"));
            await repository.RecordDemandAsync(PlaceId.From("ChIJordinary"));
        }

        await repository.SaveAsync(Analysis("ChIJwanted", 72));   // a re-analysis
        var wanted = await repository.GetAsync(PlaceId.From("ChIJwanted"));
        Assert.Equal(3, wanted!.Demand);
        Assert.Equal(72, wanted.Analysis.StrangenessScore);
        Assert.Equal([new ComicSource(1, 1, 2)], wanted.Sources);

        var candidates = await repository.GetPredrawCandidatesAsync(promptVersion: 4, minScore: 40, minDemand: 3, limit: 10);
        Assert.Equal(["ChIJwanted"], candidates.Select(c => c.PlaceId.Value));

        await repository.ResetDemandAsync(PlaceId.From("ChIJwanted"));
        Assert.Empty(await repository.GetPredrawCandidatesAsync(4, 40, 3, 10));
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task UpsertRestaurantEntity_ValidEntity_StoresSuccessfully()
    {
        // Arrange
        await _fixture.CreateTestTableAsync(TestTableName);
        var tableClient = _fixture.TableServiceClient.GetTableClient(TestTableName);

        var entity = new TableEntity("US-WA-Seattle", "ChIJtest123")
        {
            { "PlaceId", "ChIJtest123" },
            { "Name", "Test Restaurant" },
            { "Address", "123 Test St" },
            { "Latitude", 47.6062 },
            { "Longitude", -122.3321 },
            { "CachedAt", DateTimeOffset.UtcNow }
        };

        // Act
        await tableClient.UpsertEntityAsync(entity);
        var retrieved = await tableClient.GetEntityAsync<TableEntity>("US-WA-Seattle", "ChIJtest123");

        // Assert
        Assert.NotNull(retrieved.Value);
        Assert.Equal("ChIJtest123", retrieved.Value["PlaceId"]);
        Assert.Equal("Test Restaurant", retrieved.Value["Name"]);
        Assert.Equal("123 Test St", retrieved.Value["Address"]);
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task QueryRestaurantsByRegion_MultipleEntities_ReturnsFiltered()
    {
        // Arrange
        await _fixture.ClearTableAsync(TestTableName);
        var tableClient = _fixture.TableServiceClient.GetTableClient(TestTableName);

        var entities = new[]
        {
            new TableEntity("US-WA-Seattle", "Place1") { { "Name", "Seattle Restaurant 1" } },
            new TableEntity("US-WA-Seattle", "Place2") { { "Name", "Seattle Restaurant 2" } },
            new TableEntity("US-CA-SanFrancisco", "Place3") { { "Name", "SF Restaurant" } }
        };

        foreach (var entity in entities)
        {
            await tableClient.UpsertEntityAsync(entity);
        }

        // Act
        var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq 'US-WA-Seattle'");
        var results = new List<TableEntity>();
        await foreach (var entity in query)
        {
            results.Add(entity);
        }

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.Equal("US-WA-Seattle", e.PartitionKey));
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task DeleteRestaurantEntity_ExistingEntity_RemovesSuccessfully()
    {
        // Arrange
        await _fixture.ClearTableAsync(TestTableName);
        var tableClient = _fixture.TableServiceClient.GetTableClient(TestTableName);

        var entity = new TableEntity("US-WA-Seattle", "PlaceToDelete")
        {
            { "Name", "Restaurant To Delete" }
        };
        await tableClient.UpsertEntityAsync(entity);

        // Act
        await tableClient.DeleteEntityAsync("US-WA-Seattle", "PlaceToDelete");

        // Assert
        var exists = await EntityExistsAsync(tableClient, "US-WA-Seattle", "PlaceToDelete");
        Assert.False(exists);
    }


    private static async Task<bool> EntityExistsAsync(TableClient tableClient, string partitionKey, string rowKey)
    {
        try
        {
            await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
