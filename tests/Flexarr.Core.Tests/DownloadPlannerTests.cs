using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class DownloadPlannerTests
{
    private static readonly SubtitleTrack English = new("e2", 2, null, "subrip", "eng", null, false, false);
    private static readonly SubtitleTrack Sidecar = new("x0", null, "/media/file.fr.srt", "subrip", "fre", null, false, false);
    private static readonly SubtitleTrack Picture = new("e3", 3, null, "hdmv_pgs_subtitle", "eng", null, false, false);

    [Fact]
    public void AnMp4ThePhonePlays_IsSentAsItIs()
    {
        var plan = DownloadPlanner.Plan(File("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac", 2), DownloadQuality.Original, [English]);

        Assert.NotNull(plan);
        Assert.True(plan.SendAsIs);
        Assert.Null(plan.Convert);
    }

    [Fact]
    public void AnMkvThePhonePlays_IsRepackaged_WithItsTextSubtitles()
    {
        var plan = DownloadPlanner.Plan(File("matroska,webm", "hevc", "eac3", 6), DownloadQuality.Original, [English, Sidecar, Picture]);

        Assert.NotNull(plan);
        Assert.False(plan.SendAsIs);
        Assert.Null(plan.Convert);
        Assert.Equal("hevc", plan.VideoCodec);
        Assert.Equal([English, Sidecar], plan.Subtitles);
    }

    [Fact]
    public void SubtitleFilesBesideAnMp4_AreBroughtIn()
    {
        var plan = DownloadPlanner.Plan(File("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac", 2), DownloadQuality.Original, [Sidecar]);

        Assert.False(plan!.SendAsIs);
        Assert.Equal([Sidecar], plan.Subtitles);
    }

    [Fact]
    public void AnOriginalThePhoneCantPlay_IsNotOffered()
    {
        var file = File("matroska,webm", "hevc", "dts", 6);

        Assert.False(DownloadPlanner.CanKeepOriginal(file));
        Assert.Null(DownloadPlanner.Plan(file, DownloadQuality.Original, []));
        Assert.NotNull(DownloadPlanner.Plan(file, DownloadQuality.Medium, []));
    }

    [Theory]
    [InlineData(DownloadQuality.High, 1080, 6)]
    [InlineData(DownloadQuality.Medium, 720, 2)]
    [InlineData(DownloadQuality.Small, 480, 2)]
    public void Sizes_AreConvertedToH264AtTheirHeightAndBitrate(DownloadQuality quality, int height, int channels)
    {
        var plan = DownloadPlanner.Plan(File("matroska,webm", "hevc", "eac3", 6, height: 2160, bitrate: 40_000_000), quality, [English, Picture])!;

        Assert.Equal(height, plan.Convert!.TargetHeight);
        Assert.True(plan.Convert.VideoBitrate <= DownloadPlanner.Size(quality).MaxBitrate);
        Assert.Equal(channels, plan.Convert.AudioChannels);
        Assert.False(plan.Convert.CopyAudio);
        Assert.Null(plan.Convert.BurnSubtitle);
        Assert.Equal([English], plan.Subtitles);
    }

    [Fact]
    public void Estimates_GoDownWithEachSize()
    {
        var file = File("matroska,webm", "hevc", "eac3", 6, height: 2160, bitrate: 40_000_000);
        file.Size = 18_000_000_000;

        var original = DownloadPlanner.EstimateBytes(file, DownloadQuality.Original);
        var high = DownloadPlanner.EstimateBytes(file, DownloadQuality.High);
        var medium = DownloadPlanner.EstimateBytes(file, DownloadQuality.Medium);
        var small = DownloadPlanner.EstimateBytes(file, DownloadQuality.Small);

        Assert.Equal(18_000_000_000, original);
        Assert.True(original > high && high > medium && medium > small);
        // An hour at Medium is about a gigabyte.
        Assert.InRange(medium, 800_000_000, 1_100_000_000);
    }

    private static MediaFile File(string container, string video, string audio, int channels, int height = 1080, long bitrate = 8_000_000) => new()
    {
        Path = "/media/file",
        Container = container,
        VideoCodec = video,
        Width = height * 16 / 9,
        Height = height,
        Bitrate = bitrate,
        AudioCodec = audio,
        AudioChannels = channels,
        DurationSeconds = 3600,
        Streams =
        [
            new MediaStream { Index = 0, Type = StreamType.Video, Codec = video, Width = height * 16 / 9, Height = height },
            new MediaStream { Index = 1, Type = StreamType.Audio, Codec = audio, Channels = channels, IsDefault = true, Language = "eng" },
        ],
    };
}
