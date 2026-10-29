using System.Buffers.Binary;

namespace Flexarr.Streaming.Remux;

/// <summary>Just enough MP4 reading to place a fragment: its first track's timescale and its decode start.</summary>
public static class Mp4Fragments
{
    /// <summary>The first track's timescale (its mdhd), from an initialisation segment.</summary>
    public static uint? Timescale(ReadOnlySpan<byte> init)
    {
        var moov = Find(init, "moov");
        var trak = Find(moov, "trak");
        var mdhd = Find(Find(trak, "mdia"), "mdhd");
        if (mdhd.Length < 24)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
    }

    /// <summary>Where a fragment starts for a track (1, the video, unless said), in that track's timescale: its tfdt.</summary>
    public static ulong? DecodeTime(ReadOnlySpan<byte> fragment, uint trackId = 1)
    {
        var moof = Find(fragment, "moof");
        var rest = moof;
        while (rest.Length > 8)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(rest);
            if (size < 8 || size > rest.Length)
            {
                return null;
            }

            if (rest[4..8].SequenceEqual("traf"u8))
            {
                var traf = rest[8..size];
                var tfhd = Find(traf, "tfhd");
                var tfdt = Find(traf, "tfdt");
                if (tfhd.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(tfhd[4..]) == trackId && tfdt.Length >= 8)
                {
                    return tfdt[0] == 1 && tfdt.Length >= 12 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tfdt[4..]);
                }
            }

            rest = rest[size..];
        }

        return null;
    }

    /// <summary>
    /// The initialisation segment with its HEVC configuration record (hvcC) cut down to the parameter sets. ffmpeg
    /// copies every NAL array out of the file's extradata into the record, SEI messages included (x265's settings
    /// blob among them), and an Apple TV refuses an HDR stream whose record carries them; AVPlayer only needs the
    /// VPS, SPS and PPS there. A segment without an hvcC (H.264, say) comes back as it is.
    /// </summary>
    public static byte[] TrimHevcConfig(byte[] init)
    {
        ArgumentNullException.ThrowIfNull(init);
        // The path down to the record, remembering each container's size field to shrink afterwards.
        var ancestors = new List<int>();
        var offset = 0;
        var length = init.Length;
        foreach (var type in new[] { "moov", "trak", "mdia", "minf", "stbl", "stsd" })
        {
            if (!FindChild(init, offset, length, type, out var at, out var size))
            {
                return init;
            }

            ancestors.Add(at);
            offset = at + 8 + (type == "stsd" ? 8 : 0); // stsd: version/flags and entry count before the entries
            length = at + size;
        }

        var entryAt = -1;
        foreach (var entry in new[] { "hvc1", "hev1", "dvh1", "dvhe" })
        {
            if (FindChild(init, offset, length, entry, out var at, out var size))
            {
                (entryAt, length) = (at, at + size);
                break;
            }
        }

        if (entryAt < 0 || !FindChild(init, entryAt + 8 + 78, length, "hvcC", out var hvccAt, out var hvccSize) || hvccSize < 8 + 23)
        {
            return init;
        }

        ancestors.Add(entryAt);
        var record = init.AsSpan(hvccAt + 8, hvccSize - 8);
        var trimmed = TrimRecord(record);
        if (trimmed is null || trimmed.Length == record.Length)
        {
            return init;
        }

        var removed = record.Length - trimmed.Length;
        var result = new byte[init.Length - removed];
        init.AsSpan(0, hvccAt + 8).CopyTo(result);
        trimmed.CopyTo(result.AsSpan(hvccAt + 8));
        init.AsSpan(hvccAt + hvccSize).CopyTo(result.AsSpan(hvccAt + 8 + trimmed.Length));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(hvccAt), (uint)(8 + trimmed.Length));
        foreach (var at in ancestors)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(at)) - (uint)removed);
        }

        return result;
    }

    /// <summary>The record with only its VPS (32), SPS (33) and PPS (34) arrays; null when it can't be read.</summary>
    private static byte[]? TrimRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 23 || record[0] != 1)
        {
            return null;
        }

        var kept = new List<byte>(record[..22].ToArray());
        var arrays = 0;
        var at = 23;
        for (var i = 0; i < record[22]; i++)
        {
            if (at + 3 > record.Length)
            {
                return null;
            }

            var start = at;
            var nalType = record[at] & 0x3F;
            var count = BinaryPrimitives.ReadUInt16BigEndian(record[(at + 1)..]);
            at += 3;
            for (var n = 0; n < count; n++)
            {
                if (at + 2 > record.Length)
                {
                    return null;
                }

                at += 2 + BinaryPrimitives.ReadUInt16BigEndian(record[at..]);
            }

            if (at > record.Length)
            {
                return null;
            }

            if (nalType is 32 or 33 or 34)
            {
                kept.AddRange(record[start..at].ToArray());
                arrays++;
            }
        }

        kept.Insert(22, (byte)arrays);
        return [.. kept];
    }

    /// <summary>The first child box of this type between <paramref name="from"/> and <paramref name="end"/>.</summary>
    private static bool FindChild(byte[] data, int from, int end, string type, out int at, out int size)
    {
        var offset = from;
        while (offset + 8 <= end)
        {
            var boxSize = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            if (boxSize == 0)
            {
                boxSize = end - offset;
            }

            if (boxSize < 8 || offset + boxSize > end)
            {
                break;
            }

            if (data.AsSpan(offset + 4, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(type)))
            {
                (at, size) = (offset, (int)boxSize);
                return true;
            }

            offset += (int)boxSize;
        }

        (at, size) = (-1, 0);
        return false;
    }

    /// <summary>The body of the first child box of this type (empty when there's none).</summary>
    private static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> data, string type)
    {
        while (data.Length >= 8)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data);
            var header = 8;
            if (size == 1 && data.Length >= 16)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
                header = 16;
            }

            if (size == 0)
            {
                size = data.Length;
            }

            if (size < header || size > data.Length)
            {
                return [];
            }

            if (data[4] == type[0] && data[5] == type[1] && data[6] == type[2] && data[7] == type[3])
            {
                return data[header..(int)size];
            }

            data = data[(int)size..];
        }

        return [];
    }
}
