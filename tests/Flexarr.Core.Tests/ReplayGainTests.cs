using Flexarr.Core.Parsing;
using Flexarr.Core.Probing;

namespace Flexarr.Core.Tests;

public class ReplayGainTests
{
    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags) =>
        tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ReadsTheFourReplayGainTags_FromARealFlacProbe()
    {
        var probed = FfprobeMediaProbe.Parse(File.ReadAllText(Path.Combine("Fixtures", "ffprobe-flac-replaygain.json")));

        var loudness = ReplayGain.FromTags(probed.Tags);

        Assert.Equal(new Loudness(-7.52, -8.10, 0.988525, 1.0), loudness);
    }

    [Theory]
    [InlineData("-7.52 dB", -7.52)]
    [InlineData("+1.20 dB", 1.2)]
    [InlineData("-3", -3)]
    [InlineData("  -0.5 LU ", -0.5)]
    public void AGain_IsTheNumberInFront_WhateverTheUnit(string tag, double expected)
    {
        Assert.Equal(expected, ReplayGain.FromTags(Tags(("REPLAYGAIN_TRACK_GAIN", tag))).TrackGain);
    }

    [Theory]
    [InlineData("loud")]
    [InlineData("")]
    [InlineData("-7.52 dB extra")]
    public void AGainThatIsntANumber_IsLeftOut(string tag)
    {
        Assert.Null(ReplayGain.FromTags(Tags(("REPLAYGAIN_TRACK_GAIN", tag))).TrackGain);
    }

    [Fact]
    public void Id3AndMp4Spellings_AreTheSameTags()
    {
        var id3 = ReplayGain.FromTags(Tags(("replaygain_track_gain", "-5.00 dB"), ("replaygain_track_peak", "0.5")));
        Assert.Equal((-5.0, 0.5), (id3.TrackGain, id3.TrackPeak));

        var mp4 = ReplayGain.FromTags(Tags(("----:com.apple.iTunes:replaygain_album_gain", "-6.00 dB"), ("----:com.apple.iTunes:replaygain_album_peak", "0.75")));
        Assert.Equal((-6.0, 0.75), (mp4.AlbumGain, mp4.AlbumPeak));
    }

    [Fact]
    public void OpusR128Gains_AreQ78_RelativeToTheR128Reference()
    {
        // -2816/256 = -11 dB at -23 LUFS, which is -6 dB at ReplayGain's -18.
        var loudness = ReplayGain.FromTags(Tags(("R128_TRACK_GAIN", "-2816"), ("R128_ALBUM_GAIN", "-1536")));

        Assert.Equal(new Loudness(-6.0, -1.0, null, null), loudness);
    }

    [Fact]
    public void ReplayGainTags_WinOverR128Ones_WhenAFileHasBoth()
    {
        Assert.Equal(-7.0, ReplayGain.FromTags(Tags(("REPLAYGAIN_TRACK_GAIN", "-7 dB"), ("R128_TRACK_GAIN", "-2816"))).TrackGain);
    }

    [Fact]
    public void APeakOfZeroOrLess_IsNoPeak()
    {
        var loudness = ReplayGain.FromTags(Tags(("REPLAYGAIN_TRACK_PEAK", "0"), ("REPLAYGAIN_ALBUM_PEAK", "-1")));

        Assert.Null(loudness.TrackPeak);
        Assert.Null(loudness.AlbumPeak);
    }

    [Fact]
    public void NoTags_IsUnknown()
    {
        Assert.Equal(Loudness.Unknown, ReplayGain.FromTags(Tags(("artist", "Dire Straits"))));
    }
}
