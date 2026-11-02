using System.Buffers.Binary;
using PoSeeReview.Api.Features.Comics;

namespace PoSeeReview.Unit.Features;

/// <summary>The audio comic's own pieces: where the bleeps go, and a WAV a browser can play.</summary>
public sealed class AudioComicTests
{
    [Fact]
    public void Lines_SplitAroundSwearing_AndTheWavHeaderDescribesThePcm()
    {
        // Leet spellings bleep like plain ones; the words around them are spoken.
        Assert.Equal(
            [("Where is my", false), ("sh1t", true), ("soup?", false)],
            AudioComicService.SplitOnProfanity("Where is my sh1t soup?").Select(p => (p.Text, p.Bleep)));
        Assert.Equal([("Lovely soup.", false)], AudioComicService.SplitOnProfanity("Lovely soup."));

        var bleep = AudioComicService.Bleep();
        var wav = AudioComicService.Wav(bleep);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(bleep.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + bleep.Length, wav.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bleep.AsSpan(0)));   // fades in from silence, no click
    }
}
