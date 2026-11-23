using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoSeeReview.Api.Health;

namespace PoSeeReview.Unit.Features.Diagnostics;

/// <summary>
/// /health, /health/ready and /api/diag share one cached health report: anonymous visitors and
/// monitors can't turn every hit into Table and Blob Storage calls.
/// </summary>
public class CachedHealthCheckServiceTests
{
    private sealed class CountingHealthCheckService : HealthCheckService
    {
        public int Runs { get; private set; }

        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Runs++;
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["azure_table_storage"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(3), null, null, ["ready", "storage"]),
                ["skia_imaging"] = new(HealthStatus.Degraded, null, TimeSpan.FromMilliseconds(1), null, null, ["imaging"])
            };
            return Task.FromResult(new HealthReport(entries, TimeSpan.FromMilliseconds(4)));
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Runs_The_Checks_At_Most_Once_Per_Minute()
    {
        var inner = new CountingHealthCheckService();
        var clock = new ManualClock();
        using var sut = new CachedHealthCheckService(inner, clock);

        await sut.CheckHealthAsync();
        await sut.CheckHealthAsync();
        clock.Advance(CachedHealthCheckService.CacheFor - TimeSpan.FromSeconds(1));
        await sut.CheckHealthAsync();
        Assert.Equal(1, inner.Runs);

        clock.Advance(TimeSpan.FromSeconds(2));
        await sut.CheckHealthAsync();
        Assert.Equal(2, inner.Runs);
    }

    [Fact]
    public async Task A_Filtered_Call_Is_Answered_From_The_Same_Report()
    {
        var inner = new CountingHealthCheckService();
        using var sut = new CachedHealthCheckService(inner, new ManualClock());

        var full = await sut.CheckHealthAsync();
        var ready = await sut.CheckHealthAsync(r => r.Tags.Contains("ready"));

        Assert.Equal(1, inner.Runs);
        Assert.Equal(HealthStatus.Degraded, full.Status);
        Assert.Equal(["azure_table_storage"], ready.Entries.Keys);
        Assert.Equal(HealthStatus.Healthy, ready.Status);
    }
}
