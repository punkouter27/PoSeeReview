using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// Persistence and policy for moderation state. Owned by the Moderation slice; the three
/// operations other slices need are exposed through <see cref="IContentModerationGate"/>
/// (NET_RULES 2.2).
/// </summary>
public sealed class ModerationRepository(
    TableServiceClient tableServiceClient,
    IOptions<AzureStorageOptions> storageOptions,
    IOptions<ModerationOptions> moderationOptions,
    TimeProvider timeProvider,
    ILogger<ModerationRepository> logger,
    OwnerAlerts? alerts = null) : IContentModerationGate
{
    private readonly TableClient _table =
        tableServiceClient.GetTableClient(storageOptions.Value.ModerationTableName);

    /// <inheritdoc />
    public async Task<ModerationVerdict> EvaluateAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await TryGetAsync(placeId, cancellationToken);

            return row is null
                ? ModerationVerdict.Clear
                : new ModerationVerdict(row.IsHidden, row.IsSuppressed, NullIfBlank(row.Reason));
        }
        catch (RequestFailedException ex)
        {
            // Fails OPEN, and that is a deliberate trade rather than an oversight. This runs on
            // every comic read; failing closed would turn a transient storage error into "the
            // entire app is unavailable". The rows this protects are a small minority, and the
            // durable enforcement — deletion — has already happened for anything that mattered.
            logger.LogError(ex, "Moderation lookup failed for {PlaceId}; treating as clear", placeId);
            return ModerationVerdict.Clear;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RecordReportAsync(
        PlaceId placeId,
        int distinctReporters,
        CancellationToken cancellationToken = default)
    {
        var threshold = Math.Max(1, moderationOptions.Value.AutoHideReportThreshold);
        var shouldHide = false;

        await MutateAsync(placeId, row =>
        {
            row.ReportCount = Math.Max(row.ReportCount, distinctReporters);

            // Only auto-hide something a human has not already ruled on. A moderator who looked
            // at a comic and left it up must not be overridden by the next three reporters —
            // that is how a review queue becomes a voting mechanism.
            shouldHide = !row.IsHidden && !row.IsReviewed && row.ReportCount >= threshold;

            if (shouldHide)
            {
                row.IsHidden = true;
                row.Reason = $"Auto-hidden after {row.ReportCount} reports, pending review.";
                row.UpdatedBy = "system";
            }

            return true;
        }, cancellationToken);

        if (shouldHide)
        {
            logger.LogWarning(
                "Auto-hid {PlaceId} after {Count} distinct reports (threshold {Threshold})",
                placeId, distinctReporters, threshold);
            alerts?.Send("Comic auto-hidden", $"{distinctReporters} people reported it. Hidden until you review it.", placeId, urgent: true);
        }

        return shouldHide;
    }

    /// <inheritdoc />
    public async Task SuppressAsync(PlaceId placeId, string reason, CancellationToken cancellationToken = default)
    {
        var row = await TryGetAsync(placeId, cancellationToken) ?? NewRow(placeId);

        row.IsSuppressed = true;
        row.IsHidden = true;
        row.IsReviewed = true;
        row.Reason = reason;
        row.UpdatedBy = string.IsNullOrWhiteSpace(row.UpdatedBy) ? "takedown" : row.UpdatedBy;
        row.UpdatedAt = timeProvider.GetUtcNow();

        await _table.UpsertEntityAsync(row, TableUpdateMode.Replace, cancellationToken);
        logger.LogWarning("Suppressed {PlaceId}: {Reason}", placeId, reason);
    }

    /// <inheritdoc />
    public async Task FlagForReviewAsync(PlaceId placeId, string category, CancellationToken cancellationToken = default)
    {
        await MutateAsync(placeId, row =>
        {
            // Never overwrites a human verdict, and never re-flags something already on the
            // queue. A comic regenerated daily would otherwise reset its own review state every
            // morning.
            if (row.IsReviewed || row.IsHidden)
            {
                return false;
            }

            row.Reason = $"Auto-flagged by the content screen ({category}). Not hidden.";
            row.UpdatedBy = "content-screen";
            return true;
        }, cancellationToken);

        logger.LogInformation("Flagged {PlaceId} for review ({Category})", placeId, category);

        // Sent even when the row was already on the queue: each refusal or edit is its own event.
        var (title, urgent) = category switch
        {
            _ when category.StartsWith(ModerationCategories.Blocked, StringComparison.Ordinal) => ("Comic blocked by the content screen", true),
            _ when category.StartsWith(ModerationCategories.ImageDeclined, StringComparison.Ordinal) => ("Image model refused to draw a comic", true),
            _ when category.StartsWith(ModerationCategories.PromptSoftened, StringComparison.Ordinal) => ("Image prompt softened", false),
            _ when category.StartsWith(ModerationCategories.VoiceDeclined, StringComparison.Ordinal) => ("Voice model refused part of an audio comic", true),
            _ => ("Comic flagged (published)", false)
        };
        alerts?.Send(title, category, placeId, urgent);
    }


    /// <summary>
    /// Applies a moderator's decision. <paramref name="hidden"/> and <paramref name="suppressed"/>
    /// are set explicitly rather than toggled, so replaying the same request twice cannot flip
    /// the state back — a moderation control must be idempotent.
    /// </summary>
    public async Task<ModerationEntity> SetStateAsync(
        PlaceId placeId,
        bool hidden,
        bool suppressed,
        string reason,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var row = await TryGetAsync(placeId, cancellationToken) ?? NewRow(placeId);

        row.IsHidden = hidden;
        row.IsSuppressed = suppressed;
        row.IsReviewed = true;
        row.Reason = reason;
        row.UpdatedBy = actor;
        row.UpdatedAt = timeProvider.GetUtcNow();

        await _table.UpsertEntityAsync(row, TableUpdateMode.Replace, cancellationToken);

        logger.LogInformation(
            "Moderation state for {PlaceId} set to hidden={Hidden}, suppressed={Suppressed} by {Actor}",
            placeId, hidden, suppressed, actor);

        return row;
    }

    /// <summary>
    /// Every place with moderation state on record. One partition, capped — an operator page,
    /// not a user path.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModerationEntity>> GetAllAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var filter = TableClient.CreateQueryFilter<ModerationEntity>(
            e => e.PartitionKey == ModerationEntity.PartitionKeyValue);

        var rows = new Dictionary<string, ModerationEntity>(StringComparer.Ordinal);

        await foreach (var row in _table.QueryAsync<ModerationEntity>(filter, cancellationToken: cancellationToken))
        {
            rows[row.PlaceId] = row;

            if (rows.Count >= limit)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Read-modify-write under the row's ETag, for the writers that fire on their own (reports,
    /// the content screen). A blind replace from a stale read could land after a moderator's
    /// Suppress and silently undo it; a lost race re-reads and re-decides instead.
    /// </summary>
    private Task MutateAsync(PlaceId placeId, Func<ModerationEntity, bool> mutate, CancellationToken cancellationToken) =>
        TableConcurrency.RetryAsync(maxAttempts: 4, async () =>
        {
            var existing = await TryGetAsync(placeId, cancellationToken);
            var row = existing ?? NewRow(placeId);

            if (!mutate(row))
            {
                return;
            }

            row.UpdatedAt = timeProvider.GetUtcNow();

            if (existing is null)
            {
                await _table.AddEntityAsync(row, cancellationToken);
            }
            else
            {
                await _table.UpdateEntityAsync(row, existing.ETag, TableUpdateMode.Replace, cancellationToken);
            }
        });

    private ModerationEntity NewRow(PlaceId placeId) => new()
    {
        PartitionKey = ModerationEntity.PartitionKeyValue,
        RowKey = ModerationEntity.RowKeyFor(placeId),
        PlaceId = placeId.Value,
        UpdatedAt = timeProvider.GetUtcNow()
    };

    private async Task<ModerationEntity?> TryGetAsync(PlaceId placeId, CancellationToken cancellationToken)
    {
        try
        {
            return await _table.GetEntityAsync<ModerationEntity>(
                ModerationEntity.PartitionKeyValue,
                ModerationEntity.RowKeyFor(placeId),
                cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No row is the clear verdict. Most places will never have one.
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
