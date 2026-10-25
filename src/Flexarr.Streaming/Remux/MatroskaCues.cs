namespace Flexarr.Streaming.Remux;

/// <summary>
/// Reads the keyframe times of an MKV's first video track from its Cues (the index muxers write for seeking), without
/// reading the rest of the file: a few kilobytes and milliseconds, where scanning every packet takes minutes.
/// </summary>
public static class MatroskaCues
{
    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
    private const uint Info = 0x1549A966, TimecodeScale = 0x2AD7B1, Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackNumber = 0xD7, TrackType = 0x83;
    private const uint Cues = 0x1C53BB6B, CuePoint = 0xBB, CueTime = 0xB3, CueTrackPositions = 0xB7, CueTrack = 0xF7, Cluster = 0x1F43B675;
    private const long MaxHeaderElement = 4 << 20, MaxCues = 64 << 20;

    /// <summary>Keyframe times in seconds, from 0; null when the file has no Cues for its video or can't be read this way.</summary>
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
            return ReadCues(stream);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<double>? ReadCues(Stream stream)
    {
        if (ReadId(stream) != Ebml)
        {
            return null;
        }

        stream.Seek(ReadSize(stream) ?? throw new InvalidDataException(), SeekOrigin.Current);
        if (ReadId(stream) != Segment)
        {
            return null;
        }

        ReadSize(stream); // often "unknown": the segment runs to the end of the file
        var segmentStart = stream.Position;
        long? cuesAt = null;
        ulong scale = 1_000_000;
        ulong? video = null;
        byte[]? cues = null;

        // The header elements before the first cluster: where the Cues are, the time scale, and which track is video.
        while (stream.Position < stream.Length)
        {
            var id = ReadId(stream);
            var size = ReadSize(stream);
            if (id == Cluster || size is null)
            {
                break;
            }

            if (id == Cues && size <= MaxCues)
            {
                cues = ReadBytes(stream, size.Value);
                continue;
            }

            if (size > MaxHeaderElement || id is not (SeekHead or Info or Tracks))
            {
                stream.Seek(size.Value, SeekOrigin.Current);
                continue;
            }

            var body = ReadBytes(stream, size.Value);
            switch (id)
            {
                case SeekHead:
                    foreach (var (seekId, seek) in Children(body))
                    {
                        if (seekId != Seek)
                        {
                            continue;
                        }

                        var children = Children(seek).ToList();
                        if (children.FirstOrDefault(c => c.Id == SeekId).Body is { } target && UInt(target) == Cues
                            && children.FirstOrDefault(c => c.Id == SeekPosition).Body is { } position)
                        {
                            cuesAt = segmentStart + (long)UInt(position);
                        }
                    }

                    break;
                case Info:
                    if (Children(body).FirstOrDefault(c => c.Id == TimecodeScale).Body is { } timecodeScale)
                    {
                        scale = UInt(timecodeScale);
                    }

                    break;
                case Tracks:
                    // Only the track entries: a CRC-32 element (which ffmpeg writes first) isn't one, and its bytes aren't elements.
                    foreach (var (entryId, entry) in Children(body))
                    {
                        if (entryId != TrackEntry || video is not null)
                        {
                            continue;
                        }

                        var fields = Children(entry).ToList();
                        if (fields.FirstOrDefault(c => c.Id == TrackType).Body is { } type && UInt(type) == 1
                            && fields.FirstOrDefault(c => c.Id == TrackNumber).Body is { } number)
                        {
                            video = UInt(number);
                        }
                    }

                    break;
            }
        }

        if (video is null)
        {
            return null;
        }

        if (cues is null && cuesAt is { } at && at < stream.Length)
        {
            stream.Position = at;
            if (ReadId(stream) != Cues || ReadSize(stream) is not { } size || size > MaxCues)
            {
                return null;
            }

            cues = ReadBytes(stream, size);
        }

        if (cues is null)
        {
            return null;
        }

        var times = new SortedSet<double>();
        foreach (var (pointId, point) in Children(cues))
        {
            if (pointId != CuePoint)
            {
                continue;
            }

            double? time = null;
            var isVideo = false;
            foreach (var (fieldId, field) in Children(point))
            {
                if (fieldId == CueTime)
                {
                    time = UInt(field) * (double)scale / 1e9;
                }
                else if (fieldId == CueTrackPositions && Children(field).Any(c => c.Id == CueTrack && UInt(c.Body) == video))
                {
                    isVideo = true;
                }
            }

            if (isVideo && time is { } seconds)
            {
                times.Add(Math.Round(seconds, 6));
            }
        }

        if (times.Count < 2)
        {
            return null;
        }

        // The first frame is always a keyframe, whether or not it was indexed.
        var list = times.ToList();
        if (list[0] > 0.5)
        {
            list.Insert(0, 0);
        }

        return list;
    }

    /// <summary>The elements inside a master element, stopping at the first that doesn't parse (a value's bytes, say).</summary>
    private static IEnumerable<(uint Id, byte[] Body)> Children(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        while (stream.Position < stream.Length)
        {
            if (NextChild(stream) is not { } child)
            {
                yield break;
            }

            yield return child;
        }
    }

    private static (uint Id, byte[] Body)? NextChild(Stream stream)
    {
        try
        {
            var id = ReadId(stream);
            return ReadSize(stream) is { } size && size <= stream.Length - stream.Position ? (id, ReadBytes(stream, size)) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>An element ID, marker bits kept (as IDs are written in the spec).</summary>
    private static uint ReadId(Stream stream)
    {
        var first = stream.ReadByte();
        if (first < 0)
        {
            throw new EndOfStreamException();
        }

        var length = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : throw new InvalidDataException("Bad element ID");
        uint id = (uint)first;
        for (var i = 1; i < length; i++)
        {
            id = (id << 8) | (uint)ReadByte(stream);
        }

        return id;
    }

    /// <summary>An element size; null for "unknown" (all ones).</summary>
    private static long? ReadSize(Stream stream)
    {
        var first = ReadByte(stream);
        var length = 1;
        var mask = 0x80;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            throw new InvalidDataException("Bad element size");
        }

        long value = first & (mask - 1);
        var unknown = value == mask - 1;
        for (var i = 1; i < length; i++)
        {
            var next = ReadByte(stream);
            unknown &= next == 0xFF;
            value = (value << 8) | (long)next;
        }

        return unknown ? null : value;
    }

    private static int ReadByte(Stream stream) => stream.ReadByte() is var b and >= 0 ? b : throw new EndOfStreamException();

    private static byte[] ReadBytes(Stream stream, long size)
    {
        if (size < 0 || size > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Element runs past the end");
        }

        var buffer = new byte[size];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ulong UInt(byte[] data)
    {
        ulong value = 0;
        foreach (var b in data.AsSpan(0, Math.Min(data.Length, 8)))
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
