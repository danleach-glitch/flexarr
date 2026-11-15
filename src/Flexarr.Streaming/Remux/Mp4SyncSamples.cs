using System.Buffers.Binary;

namespace Flexarr.Streaming.Remux;

/// <summary>
/// Reads the keyframe times of an MP4's (or MOV's) first video track from its sample tables (stss, the sync samples, with
/// stts, ctts and the edit list for their times), without reading the media: the MP4 counterpart of
/// <see cref="MatroskaCues"/>. The times are in the file's own timeline, as ffprobe's packet scan gives them.
/// </summary>
public static class Mp4SyncSamples
{
    private const long MaxMoov = 256L << 20;

    /// <summary>Keyframe times in seconds; null when the file isn't an MP4 with a sync-sample table for its video.</summary>
    public static IReadOnlyList<double>? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        return Read(stream);
    }

    public static IReadOnlyList<double>? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return FindMoov(stream) is { } moov ? FromMoov(moov) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The top-level boxes, skipped by their sizes (the media data isn't read), until the movie box.</summary>
    private static byte[]? FindMoov(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        var first = true;
        while (stream.Position + 8 <= stream.Length)
        {
            var at = stream.Position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Type(header[4..8]);
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - at;
            }

            // The first box of an MP4 is ftyp (or, in older QuickTime files, one of a few others).
            if (first && type is not ("ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot"))
            {
                return null;
            }

            first = false;
            if (size < headerSize || at + size > stream.Length)
            {
                return null;
            }

            if (type == "moov")
            {
                if (size - headerSize > MaxMoov)
                {
                    return null;
                }

                var moov = new byte[size - headerSize];
                stream.ReadExactly(moov);
                return moov;
            }

            stream.Position = at + size;
        }

        return null;
    }

    private static List<double>? FromMoov(byte[] moov)
    {
        uint movieScale = 0;
        foreach (var (type, body) in Boxes(moov))
        {
            if (type == "mvhd")
            {
                movieScale = body[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12));
            }
        }

        // Fragmented files keep their samples in the fragments, with nothing in the tables here.
        if (Boxes(moov).Any(b => b.Type == "mvex"))
        {
            return null;
        }

        foreach (var (type, trak) in Boxes(moov))
        {
            if (type == "trak" && FromVideoTrack(trak, movieScale) is { } times)
            {
                return times;
            }
        }

        return null;
    }

    private static List<double>? FromVideoTrack(byte[] trak, uint movieScale)
    {
        var mdia = Child(trak, "mdia");
        if (mdia is null || Child(mdia, "hdlr") is not { Length: >= 12 } hdlr || Type(hdlr.AsSpan(8, 4)) != "vide")
        {
            return null;
        }

        var mdhd = Child(mdia, "mdhd") ?? throw new InvalidDataException();
        var scale = mdhd[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(20)) : BinaryPrimitives.ReadUInt32BigEndian(mdhd.AsSpan(12));
        var stbl = Child(Child(mdia, "minf") ?? throw new InvalidDataException(), "stbl") ?? throw new InvalidDataException();
        // No sync-sample table means every sample is a keyframe (all-intra video): not worth listing, and rare.
        if (scale == 0 || Child(stbl, "stss") is not { } stss || Child(stbl, "stts") is not { } stts)
        {
            return null;
        }

        var syncCount = BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(4));
        if (syncCount == 0)
        {
            return null;
        }

        var syncs = new uint[syncCount];
        for (var i = 0; i < syncCount; i++)
        {
            syncs[i] = BinaryPrimitives.ReadUInt32BigEndian(stss.AsSpan(8 + (4 * i)));
        }

        var decode = Runs(stts);
        // Composition offsets read as signed whatever the box version, as ffmpeg does: some muxers write negative ones in version 0.
        var composition = Child(stbl, "ctts") is { } ctts ? Runs(ctts, signed: true) : null;
        var (shift, padding) = EditOffset(trak, movieScale, scale);

        var times = new List<double>(syncs.Length);
        long dts = 0;
        long sample = 1; // 1-based, as stss counts
        var decodeRun = 0;
        long leftInRun = decode.Count > 0 ? decode[0].Count : 0;
        var compositionRun = 0;
        long leftInComposition = composition is { Count: > 0 } ? composition[0].Count : 0;
        foreach (var sync in syncs)
        {
            // Walk the runs to this sample, adding up the decode times.
            while (sample < sync)
            {
                if (decodeRun >= decode.Count)
                {
                    throw new InvalidDataException();
                }

                var step = Math.Min(leftInRun, sync - sample);
                dts += step * decode[decodeRun].Delta;
                sample += step;
                leftInRun -= step;
                if (leftInRun == 0 && ++decodeRun < decode.Count)
                {
                    leftInRun = decode[decodeRun].Count;
                }

                if (composition is not null)
                {
                    var remaining = step;
                    while (remaining > 0 && compositionRun < composition.Count)
                    {
                        var take = Math.Min(leftInComposition, remaining);
                        remaining -= take;
                        leftInComposition -= take;
                        if (leftInComposition == 0 && ++compositionRun < composition.Count)
                        {
                            leftInComposition = composition[compositionRun].Count;
                        }
                    }
                }
            }

            var offset = composition is not null && compositionRun < composition.Count ? composition[compositionRun].Delta : 0;
            times.Add(Math.Round((dts + offset - shift + padding) / (double)scale, 6));
        }

        times.Sort();
        return times;
    }

    /// <summary>
    /// The edit list's effect on presentation times, in the media's time scale: the media time the first real edit starts
    /// at (taken off), and any empty edits before it (a delay, added on).
    /// </summary>
    private static (long Shift, long Padding) EditOffset(byte[] trak, uint movieScale, uint mediaScale)
    {
        if (Child(trak, "edts") is not { } edts || Child(edts, "elst") is not { } elst)
        {
            return (0, 0);
        }

        var wide = elst[0] == 1;
        var count = BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(4));
        var entry = wide ? 20 : 12;
        long padding = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 8 + (i * entry);
            var duration = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(elst.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(elst.AsSpan(at));
            var mediaTime = wide ? BinaryPrimitives.ReadInt64BigEndian(elst.AsSpan(at + 8)) : BinaryPrimitives.ReadInt32BigEndian(elst.AsSpan(at + 4));
            if (mediaTime == -1)
            {
                padding += movieScale == 0 ? 0 : duration * mediaScale / movieScale;
                continue;
            }

            return (mediaTime, padding);
        }

        return (0, padding);
    }

    /// <summary>An stts or ctts table: (sample count, value) runs.</summary>
    private static List<(long Count, long Delta)> Runs(byte[] box, bool signed = false)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(4));
        if (8 + (count * 8L) > box.Length)
        {
            throw new InvalidDataException();
        }

        var runs = new List<(long, long)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var at = 8 + (i * 8);
            var value = signed ? BinaryPrimitives.ReadInt32BigEndian(box.AsSpan(at + 4)) : (long)BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(at + 4));
            runs.Add((BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(at)), value));
        }

        return runs;
    }

    private static byte[]? Child(byte[] parent, string type) => Boxes(parent).Where(b => b.Type == type).Select(b => b.Body).FirstOrDefault();

    /// <summary>The boxes inside a box's body.</summary>
    private static IEnumerable<(string Type, byte[] Body)> Boxes(byte[] data)
    {
        var at = 0;
        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            var type = Type(data.AsSpan(at + 4, 4));
            var headerSize = 8;
            if (size == 1)
            {
                if (at + 16 > data.Length)
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = data.Length - at;
            }

            if (size < headerSize || at + size > data.Length)
            {
                throw new InvalidDataException();
            }

            yield return (type, data[(at + headerSize)..(int)(at + size)]);
            at += (int)size;
        }
    }

    private static string Type(ReadOnlySpan<byte> four) => System.Text.Encoding.ASCII.GetString(four);
}
