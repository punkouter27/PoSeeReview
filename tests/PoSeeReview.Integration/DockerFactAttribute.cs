using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace PoSeeReview.Integration;

/// <summary>
/// [Fact] for the Integration tier: skips with one clear reason when Docker is not running locally,
/// instead of failing every test with a Testcontainers DockerUnavailableException. In CI
/// (<c>CI=true</c>) it never skips: a missing Docker there is a failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>[Theory] counterpart of <see cref="DockerFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (DockerAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Asks the Docker CLI once per test run whether a Docker engine answers.</summary>
public static class DockerAvailability
{
    private const string Message =
        "Docker is not running, so the Integration tier is skipped (it needs Docker for Testcontainers). " +
        "Start Docker Desktop and run the tests again.";

    private static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when Docker answers (or in CI); otherwise why the tier is skipped.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Probe()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return Message;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(entireProcessTree: true);
                return Message;
            }

            return process.ExitCode == 0 ? null : Message;
        }
        catch (Win32Exception)
        {
            // No docker CLI on PATH.
            return Message;
        }
        catch (InvalidOperationException)
        {
            return Message;
        }
    }
}
