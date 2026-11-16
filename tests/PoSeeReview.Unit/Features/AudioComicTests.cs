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

    [Fact]
    public void Normalisation_BringsAQuietAndALoudVoiceToTheSameLoudness_WithoutClipping()
    {
        // A soft voice with long pauses (which must not count as quietness), and a hot one.
        byte[] quiet = [.. Tone(0.02, 0.5), .. new byte[24_000 * 2], .. Tone(0.02, 0.5)];
        var loud = Tone(0.6, 1.0);

        foreach (var voice in new[] { quiet, loud })
        {
            AudioComicService.ApplyGain(voice, AudioComicService.GainToTarget(AudioComicService.SpeechRms([voice])));
        }

        var quietDb = 20 * Math.Log10(AudioComicService.SpeechRms([quiet]));
        var loudDb = 20 * Math.Log10(AudioComicService.SpeechRms([loud]));
        Assert.InRange(quietDb - loudDb, -1.0, 1.0);
        Assert.InRange(quietDb, -21.5, -18.5);   // TargetRms is -20 dBFS

        // Boosting 8x past full scale bends the peaks under it instead of wrapping around.
        var hot = Tone(0.9, 0.1);
        AudioComicService.ApplyGain(hot, 8);
        for (var i = 0; i < hot.Length; i += 2)
        {
            Assert.InRange(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(hot.AsSpan(i))), 0, short.MaxValue);
        }

        // The bleep sits at the speech level, not over it.
        Assert.InRange(20 * Math.Log10(AudioComicService.SpeechRms([AudioComicService.Bleep()])), -21.5, -18.5);
    }

    /// <summary>A 220 Hz sine at <paramref name="amplitude"/> of full scale, as 24 kHz 16-bit PCM.</summary>
    private static byte[] Tone(double amplitude, double seconds)
    {
        var samples = (int)(24_000 * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
                (short)(Math.Sin(2 * Math.PI * 220 * i / 24_000.0) * amplitude * short.MaxValue));
        }

        return pcm;
    }
}
