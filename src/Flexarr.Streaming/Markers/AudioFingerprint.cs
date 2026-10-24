using System.Numerics;

namespace Flexarr.Streaming.Markers;

/// <summary>A segment that two recordings share, in seconds from the start of each analysed window.</summary>
public sealed record SharedSegment(double StartA, double EndA, double StartB, double EndB)
{
    public double Duration => EndA - StartA;
}

/// <summary>
/// Compact audio fingerprints in the style of Haitsma &amp; Kalker: every 8 ms, one bit per pair of adjacent
/// frequency bands recording whether the energy difference between them grew or shrank since the previous frame.
/// Identical audio (a show's theme tune) produces near-identical bit patterns even after re-encoding.
/// </summary>
public static class AudioFingerprint
{
    public const int SampleRate = 8000;
    private const int FrameSize = 1024;
    // Frames overlap heavily (1/16 hop) so two recordings never sit more than 4 ms out of alignment;
    // coarser hops make the same audio at a non-multiple offset look unrelated.
    private const int Hop = 64;
    private const int Bands = 33;
    private const double MinFrequency = 300;
    private const double MaxFrequency = 3000;

    public static double SecondsPerFrame => (double)Hop / SampleRate;

    /// <summary>Fingerprints mono 16-bit PCM at <see cref="SampleRate"/>.</summary>
    public static uint[] Compute(ReadOnlySpan<short> samples)
    {
        var frames = samples.Length < FrameSize ? 0 : ((samples.Length - FrameSize) / Hop) + 1;
        var result = new uint[Math.Max(0, frames - 1)];
        if (frames < 2)
        {
            return result;
        }

        var window = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1)));
        }

        // FFT bin ranges for logarithmically spaced bands.
        var edges = new int[Bands + 1];
        for (var b = 0; b <= Bands; b++)
        {
            var frequency = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)b / Bands);
            edges[b] = (int)Math.Round(frequency * FrameSize / SampleRate);
        }

        var buffer = new Complex[FrameSize];
        var previous = new double[Bands];
        var current = new double[Bands];

        for (var f = 0; f < frames; f++)
        {
            var offset = f * Hop;
            for (var i = 0; i < FrameSize; i++)
            {
                buffer[i] = new Complex(samples[offset + i] * window[i], 0);
            }

            Fft(buffer);
            for (var b = 0; b < Bands; b++)
            {
                double energy = 0;
                for (var k = edges[b]; k < Math.Max(edges[b + 1], edges[b] + 1); k++)
                {
                    energy += buffer[k].Real * buffer[k].Real + buffer[k].Imaginary * buffer[k].Imaginary;
                }

                current[b] = energy;
            }

            if (f > 0)
            {
                uint bits = 0;
                for (var b = 0; b < Bands - 1; b++)
                {
                    var delta = current[b] - current[b + 1] - (previous[b] - previous[b + 1]);
                    if (delta > 0)
                    {
                        bits |= 1u << b;
                    }
                }

                result[f - 1] = bits;
            }

            (previous, current) = (current, previous);
        }

        return result;
    }

    /// <summary>
    /// Finds the longest stretch of audio common to both fingerprints (at least <paramref name="minSeconds"/>,
    /// at most <paramref name="maxSeconds"/>), or null if there isn't one.
    /// </summary>
    public static SharedSegment? FindShared(uint[] a, uint[] b, double minSeconds = 15, double maxSeconds = 180)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return null;
        }

        // 1. Vote for alignments: exact sub-fingerprint matches are rare by chance but common within shared audio.
        var index = new Dictionary<uint, List<int>>();
        for (var j = 0; j < b.Length; j++)
        {
            if (b[j] is 0 or uint.MaxValue)
            {
                continue; // Silence and clipping produce uninformative patterns.
            }

            if (!index.TryGetValue(b[j], out var list))
            {
                index[b[j]] = list = [];
            }

            list.Add(j);
        }

        var votes = new Dictionary<int, int>();
        for (var i = 0; i < a.Length; i++)
        {
            if (index.TryGetValue(a[i], out var matches) && matches.Count < 50)
            {
                foreach (var j in matches)
                {
                    votes[j - i] = votes.GetValueOrDefault(j - i) + 1;
                }
            }
        }

        var minFrames = (int)(minSeconds / SecondsPerFrame);
        var maxFrames = (int)(maxSeconds / SecondsPerFrame);
        SharedSegment? best = null;

        // 2. For the strongest alignments, find the longest run where the patterns stay close.
        foreach (var offset in votes.Where(v => v.Value >= 8).OrderByDescending(v => v.Value).Take(5).Select(v => v.Key))
        {
            var start = Math.Max(0, -offset);
            var end = Math.Min(a.Length, b.Length - offset);
            if (end - start < minFrames)
            {
                continue;
            }

            // Smoothed bit error over ~1 second; shared audio sits well below the ~16 bits expected by chance.
            var smooth = (int)(1 / SecondsPerFrame);
            const double threshold = 10.0;
            var errors = new int[end - start];
            for (var i = start; i < end; i++)
            {
                errors[i - start] = BitOperations.PopCount(a[i] ^ b[i + offset]);
            }

            var runStart = -1;
            double sum = 0;
            for (var i = 0; i <= errors.Length; i++)
            {
                if (i < errors.Length)
                {
                    sum += errors[i];
                    if (i >= smooth)
                    {
                        sum -= errors[i - smooth];
                    }
                }

                var good = i < errors.Length && i >= smooth - 1 && sum / smooth <= threshold;
                if (good && runStart < 0)
                {
                    runStart = i - smooth + 1;
                }
                else if (!good && runStart >= 0)
                {
                    var length = i - runStart;
                    if (length >= minFrames && length <= maxFrames && (best is null || length * SecondsPerFrame > best.Duration))
                    {
                        var aStart = start + runStart;
                        best = new SharedSegment(
                            aStart * SecondsPerFrame, (aStart + length) * SecondsPerFrame,
                            (aStart + offset) * SecondsPerFrame, (aStart + offset + length) * SecondsPerFrame);
                    }

                    runStart = -1;
                }
            }
        }

        return best;
    }

    /// <summary>In-place iterative radix-2 FFT; length must be a power of two.</summary>
    private static void Fft(Complex[] data)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wLength = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + (length / 2)] * w;
                    data[i + k] = u + v;
                    data[i + k + (length / 2)] = u - v;
                    w *= wLength;
                }
            }
        }
    }
}
