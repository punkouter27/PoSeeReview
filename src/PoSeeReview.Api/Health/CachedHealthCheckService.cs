using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoSeeReview.Api.Health;

/// <summary>
/// Wraps the framework's <see cref="HealthCheckService"/> so <c>/health</c>, <c>/health/ready</c> and
/// <c>/api/diag</c> share ONE report, re-run at most once per <see cref="CacheFor"/>. All three are
/// anonymous, and every run reaches Table and Blob Storage; without this a visitor pressing Refresh
/// (or a monitor polling) would make a storage call per hit. A filtered call (the <c>ready</c> tag)
/// is answered from the same full report, so it never triggers a second run either.
/// </summary>
public sealed class CachedHealthCheckService(HealthCheckService inner, TimeProvider timeProvider) : HealthCheckService, IDisposable
{
    /// <summary>How long one health report is reused.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, HealthReport Report)? _cache;

    public void Dispose() => _gate.Dispose();

    public override async Task<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate,
        CancellationToken cancellationToken = default)
    {
        var full = await FullReportAsync(cancellationToken);
        if (predicate is null)
        {
            return full;
        }

        // The predicate looks at a registration's name and tags; both are on the cached entry.
        var entries = full.Entries
            .Where(e => predicate(new HealthCheckRegistration(e.Key, _ => NoCheck.Instance, null, e.Value.Tags)))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return new HealthReport(entries, full.TotalDuration);
    }

    private async Task<HealthReport> FullReportAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } cached && timeProvider.GetUtcNow() - cached.At < CacheFor)
            {
                return cached.Report;
            }

            var report = await inner.CheckHealthAsync(predicate: null, cancellationToken);
            _cache = (timeProvider.GetUtcNow(), report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Placeholder check for the registration handed to a predicate; it is never run.</summary>
    private sealed class NoCheck : IHealthCheck
    {
        public static readonly NoCheck Instance = new();

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }
}

/// <summary>Registers <see cref="CachedHealthCheckService"/> over the framework's health check service.</summary>
internal static class CachedHealthCheckServiceExtensions
{
    /// <summary>Call after <c>AddHealthChecks()</c>: every consumer of <see cref="HealthCheckService"/> then gets the cached one.</summary>
    public static IServiceCollection AddCachedHealthChecks(this IServiceCollection services)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(HealthCheckService) && d.ImplementationType is not null)
            ?? throw new InvalidOperationException("AddHealthChecks() must run before AddCachedHealthChecks().");
        var innerType = original.ImplementationType!;

        services.RemoveAll<HealthCheckService>();
        services.AddSingleton(sp => new CachedHealthCheckService(
            (HealthCheckService)ActivatorUtilities.CreateInstance(sp, innerType),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<HealthCheckService>(sp => sp.GetRequiredService<CachedHealthCheckService>());
        return services;
    }
}
