namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// A conversation overheard at the place: one WAV and its script, timed. No narrator; the
/// script is model-written talk sourced from the reviews (a dramatization, not quotes); swearing is bleeped in the audio and
/// shown as "[bleep]" here.
/// </summary>
public sealed class AudioComicDto
{
    /// <summary>Same-origin URL of the WAV.</summary>
    public string AudioUrl { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public List<AudioComicLineDto> Lines { get; set; } = [];
}

/// <summary>One spoken line, with where it sits in the audio and which panel it belongs to.</summary>
public sealed class AudioComicLineDto
{
    public string Speaker { get; set; } = string.Empty;

    /// <summary>1-based panel the line plays over.</summary>
    public int Panel { get; set; }

    public string Text { get; set; } = string.Empty;

    public double Start { get; set; }

    public double End { get; set; }
}
