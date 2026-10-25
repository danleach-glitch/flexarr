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

    /// <summary>Where a fragment starts for track 1 (the video), in that track's timescale: its tfdt.</summary>
    public static ulong? DecodeTime(ReadOnlySpan<byte> fragment)
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
                if (tfhd.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(tfhd[4..]) == 1 && tfdt.Length >= 8)
                {
                    return tfdt[0] == 1 && tfdt.Length >= 12 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tfdt[4..]);
                }
            }

            rest = rest[size..];
        }

        return null;
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
