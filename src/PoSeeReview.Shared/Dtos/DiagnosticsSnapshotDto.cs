namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// Body of <c>GET /api/diag</c>, rendered by the anonymous <c>/diag</c> page. Everything here
/// is safe to show a stranger: statuses, timings, plain-English purposes, the build stamp and the
/// environment name. Never a connection string, key, URI, host or tenant id, path, user id, or an
/// exception message — a failing check carries its exception TYPE at most.
/// </summary>
public sealed class DiagnosticsSnapshotDto
{
    /// <summary>When the server ran the checks (UTC).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Hosting environment name (Development, Test, Production).</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary><c>YYMMDD</c> build stamp of the API assembly.</summary>
    public string BuildStamp { get; set; } = string.Empty;

    /// <summary>Worst status across every check: Healthy, Degraded or Unhealthy.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Server-side time spent running the checks.</summary>
    public double DurationMilliseconds { get; set; }

    /// <summary>True when the app runs on mocked AI and place data (UseMockData).</summary>
    public bool IsMockData { get; set; }

    /// <summary>One row per store and per external service.</summary>
    public List<DiagnosticsCheckDto> Checks { get; set; } = [];
}

/// <summary>One dependency on <c>/api/diag</c>.</summary>
public sealed class DiagnosticsCheckDto
{
    /// <summary>"storage" (a database or file store) or "service" (an external dependency).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Display name, e.g. "Table Storage".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>One line on what the app uses it for.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Healthy, Degraded or Unhealthy.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>A short plain-English state ("Reachable", "Configured", "Mocked", "Not configured").</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>How long the probe took; null when nothing was probed (a configuration check).</summary>
    public double? DurationMilliseconds { get; set; }

    /// <summary>Exception type name of a failed probe (never its message).</summary>
    public string? ErrorType { get; set; }

    /// <summary>When this check ran (UTC).</summary>
    public DateTime CheckedAt { get; set; }
}

/// <summary>Values of <see cref="DiagnosticsCheckDto.Kind"/>.</summary>
public static class DiagnosticsCheckKinds
{
    public const string Storage = "storage";
    public const string Service = "service";
}
