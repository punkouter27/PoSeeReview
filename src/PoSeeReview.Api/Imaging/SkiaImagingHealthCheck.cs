using Microsoft.Extensions.Diagnostics.HealthChecks;
using SkiaSharp;

namespace PoSeeReview.Api.Imaging;

/// <summary>
/// Proves the Skia native library loads on this host and the embedded fonts parse.
/// <para>
/// Both failure modes are otherwise silent: the caption overlay hands back the uncaptioned image
/// and the share card 404s, by design, so a missing <c>libSkiaSharp.so</c> after a deploy would
/// only show up as every comic losing its captions. Unhealthy here fails the deploy's smoke run
/// (it polls <c>/health</c>) without touching App Service's own probe (<c>/health/live</c>).
/// Computed once: the answer cannot change while the process lives.
/// </para>
/// </summary>
public sealed class SkiaImagingHealthCheck : IHealthCheck
{
    private static readonly Lazy<HealthCheckResult> Result = new(Probe);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Value);

    private static HealthCheckResult Probe()
    {
        try
        {
            using var bitmap = new SKBitmap(64, 32);
            using var canvas = new SKCanvas(bitmap);
            using var caption = SkiaText.Font(SkiaText.Caption, 16);
            using var card = SkiaText.Font(SkiaText.Card, 16);
            SkiaText.DrawLines(canvas, ["Ag"], 0, 16, SKTextAlign.Left, caption, SKColors.Black);
            SkiaText.DrawLines(canvas, ["Ag"], 32, 16, SKTextAlign.Left, card, SKColors.Black);
            using var webp = bitmap.Encode(SKEncodedImageFormat.Webp, 85);

            return webp is { Size: > 0 }
                ? HealthCheckResult.Healthy("Skia renders and encodes with the embedded fonts")
                : HealthCheckResult.Unhealthy("Skia could not encode WebP");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Skia imaging is unavailable", ex);
        }
    }
}
