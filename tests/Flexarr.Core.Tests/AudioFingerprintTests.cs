using Flexarr.Streaming.Markers;

namespace Flexarr.Core.Tests;

public class AudioFingerprintTests
{
    private const int Rate = AudioFingerprint.SampleRate;

    [Fact]
    public void FindsThemeTuneSharedByTwoEpisodes()
    {
        var theme = Music(seed: 1, seconds: 40);
        var episode1 = Concat(Speech(seed: 2, seconds: 30), theme, Speech(seed: 3, seconds: 60));
        // 37 extra samples: the theme doesn't start on a frame boundary in the second episode.
        var episode2 = Concat(Speech(seed: 4, seconds: 12, extraSamples: 37), Noisy(theme, seed: 9), Speech(seed: 5, seconds: 80));

        var shared = AudioFingerprint.FindShared(AudioFingerprint.Compute(episode1), AudioFingerprint.Compute(episode2));

        Assert.NotNull(shared);
        Assert.InRange(shared.StartA, 28, 32);
        Assert.InRange(shared.EndA, 68, 72);
        Assert.InRange(shared.StartB, 10, 14);
        Assert.InRange(shared.Duration, 36, 42);
    }

    [Fact]
    public void UnrelatedAudioSharesNothing()
    {
        var a = AudioFingerprint.Compute(Concat(Speech(seed: 6, seconds: 60), Music(seed: 7, seconds: 60)));
        var b = AudioFingerprint.Compute(Concat(Music(seed: 8, seconds: 60), Speech(seed: 10, seconds: 60)));

        Assert.Null(AudioFingerprint.FindShared(a, b));
    }

    /// <summary>A theme tune: chords with harmonics, changing on the beat, plus percussive noise bursts.</summary>
    private static short[] Music(int seed, int seconds)
    {
        var random = new Random(seed);
        var samples = new short[seconds * Rate];
        double[] notes = [];
        double envelope = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            if (i % (Rate / 2) == 0)
            {
                notes = [.. Enumerable.Range(0, 3).Select(_ => 150 + random.NextDouble() * 600)];
                envelope = 1;
            }

            envelope *= 0.9995;
            var t = (double)i / Rate;
            double value = 0;
            foreach (var f in notes)
            {
                for (var h = 1; h <= 6; h++)
                {
                    value += Math.Sin(2 * Math.PI * f * h * t) / h;
                }
            }

            value += envelope * (random.NextDouble() - 0.5) * 3;
            samples[i] = (short)(value * 2500);
        }

        return samples;
    }

    /// <summary>Band-limited noise bursts: unstructured audio standing in for dialogue.</summary>
    private static short[] Speech(int seed, int seconds, int extraSamples = 0)
    {
        var random = new Random(seed);
        var samples = new short[(seconds * Rate) + extraSamples];
        double level = 0, smoothed = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            if (i % (Rate / 5) == 0)
            {
                level = random.NextDouble();
            }

            smoothed = (0.7 * smoothed) + (0.3 * (random.NextDouble() - 0.5));
            samples[i] = (short)(smoothed * level * 20000);
        }

        return samples;
    }

    /// <summary>The same audio from a different encode: a gain change plus noise about 35 dB below the signal.</summary>
    private static short[] Noisy(short[] source, int seed)
    {
        var random = new Random(seed);
        return [.. source.Select(s => (short)Math.Clamp(s * 0.93 + s * 0.018 * (random.NextDouble() * 2 - 1) + (random.NextDouble() - 0.5) * 80, short.MinValue, short.MaxValue))];
    }

    private static short[] Concat(params short[][] parts) => [.. parts.SelectMany(p => p)];
}
