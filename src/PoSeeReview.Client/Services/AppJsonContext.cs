using System.Text.Json.Serialization;
using PoSeeReview.Client.Components;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Client.Services;

/// <summary>
/// Source-generated System.Text.Json metadata for every DTO the client (de)serializes.
/// Keeps the trimmable WASM client free of reflection-based serialization (NET_RULES 6.6).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NearbyRestaurantsResponse))]
[JsonSerializable(typeof(ComicDto))]
[JsonSerializable(typeof(AudioComicDto))]
[JsonSerializable(typeof(AudioComicLineDto))]
[JsonSerializable(typeof(ScoreBreakdownDto))]
[JsonSerializable(typeof(ComicSourceDto))]
[JsonSerializable(typeof(ComicGenerationEventDto))]
[JsonSerializable(typeof(LeaderboardResponse))]
[JsonSerializable(typeof(HallOfFameResponse))]
[JsonSerializable(typeof(HealthStatusDto))]
[JsonSerializable(typeof(DiagnosticsSnapshotDto))]
[JsonSerializable(typeof(MockStatusDto))]
[JsonSerializable(typeof(AuthStateDto))]
[JsonSerializable(typeof(GenerationBudgetDto))]
[JsonSerializable(typeof(ComicReportRequestDto))]
[JsonSerializable(typeof(ComicReportResponseDto))]
[JsonSerializable(typeof(ShareLinkDto))]
[JsonSerializable(typeof(ModerationQueueDto))]
[JsonSerializable(typeof(ModerationActionDto))]
[JsonSerializable(typeof(ModerationTriageDto))]
[JsonSerializable(typeof(CachedComicsResponse))]
// Not a wire DTO: the locally-stored comic history (ComicHistoryService).
[JsonSerializable(typeof(List<ComicHistoryEntry>))]
// Not a wire DTO: the remembered leaderboard ranks (BoardMemoryService).
[JsonSerializable(typeof(Dictionary<string, int>))]
// Not a wire DTO: the pins handed to map-view.js (MapView).
[JsonSerializable(typeof(List<MapPin>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
