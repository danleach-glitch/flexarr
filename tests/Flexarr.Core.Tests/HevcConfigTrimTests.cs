using System.Buffers.Binary;
using System.Text;
using Flexarr.Streaming.Remux;

namespace Flexarr.Core.Tests;

/// <summary>An Apple TV refuses an HDR stream whose hvcC carries SEI arrays; only the parameter sets stay.</summary>
public class HevcConfigTrimTests
{
    private static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Array(byte nalType, params byte[][] nals)
    {
        var bytes = new List<byte> { (byte)(0x80 | nalType), 0, (byte)nals.Length };
        foreach (var nal in nals)
        {
            bytes.AddRange([(byte)(nal.Length >> 8), (byte)nal.Length]);
            bytes.AddRange(nal);
        }

        return [.. bytes];
    }

    private static byte[] Record(params byte[][] arrays)
    {
        var header = Convert.FromHexString("01022000000090000000000099f000fcfdfafa00000f");
        return [.. header, (byte)arrays.Length, .. arrays.SelectMany(a => a)];
    }

    private static byte[] Init(byte[] record)
    {
        var hvcC = Box("hvcC", record);
        var entry = Box("hvc1", new byte[78], hvcC, Box("colr", new byte[11]));
        var stsd = Box("stsd", new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, entry);
        var stbl = Box("stbl", stsd, Box("stts", new byte[8]));
        var trak = Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("mdhd", new byte[24]), Box("minf", stbl)));
        var audio = Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd", new byte[8], Box("ec-3", new byte[28]))))));
        return [.. Box("ftyp", new byte[24]), .. Box("moov", Box("mvhd", new byte[100]), trak, audio, Box("mvex", new byte[8]))];
    }

    [Fact]
    public void SeiArrays_AreDropped_AndEveryContainerShrinksToMatch()
    {
        var vps = new byte[25];
        var sps = new byte[58];
        var pps = new byte[9];
        var sei = new byte[2649];
        var before = Init(Record(Array(32, vps), Array(33, sps), Array(34, pps), Array(39, new byte[9], sei, new byte[6])));

        var after = Mp4Fragments.TrimHevcConfig(before);

        Assert.Equal(before.Length - (3 + 2 + 9 + 2 + 2649 + 2 + 6), after.Length);
        Assert.Equal(Init(Record(Array(32, vps), Array(33, sps), Array(34, pps))), after);
        Assert.True(WellFormed(after), "box sizes add up");
        // Idempotent, and an H.264 init (no hvcC) is untouched.
        Assert.Equal(after, Mp4Fragments.TrimHevcConfig(after));
        var avc = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stsd", new byte[8], Box("avc1", new byte[78], Box("avcC", new byte[20]))))))));
        Assert.Equal(avc, Mp4Fragments.TrimHevcConfig(avc));
    }

    [Fact]
    public void ARecordWithOnlyParameterSets_IsLeftAlone()
    {
        var init = Init(Record(Array(32, new byte[25]), Array(33, new byte[58]), Array(34, new byte[9])));
        Assert.Same(init, Mp4Fragments.TrimHevcConfig(init));
    }

    /// <summary>Every box's declared size lands exactly on the next box, down through the containers.</summary>
    private static bool WellFormed(byte[] data)
    {
        bool Walk(int from, int end)
        {
            var at = from;
            while (at < end)
            {
                if (at + 8 > end) return false;
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                var type = Encoding.ASCII.GetString(data, at + 4, 4);
                if (size < 8 || at + size > end) return false;
                if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" && !Walk(at + 8, at + size)) return false;
                if (type == "stsd" && !Walk(at + 16, at + size)) return false;
                if (type == "hvc1" && !Walk(at + 8 + 78, at + size)) return false;
                at += size;
            }

            return at == end;
        }

        return Walk(0, data.Length);
    }
}
