using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

/// <summary>Which version plays: the devices here are made up to differ the way real ones do.</summary>
public class VersionPickerTests
{
    private static MediaFile File(string name, int height, string container = "mkv", string codec = "hevc", VideoRange range = VideoRange.Sdr, long bitrate = 8_000_000,
        double duration = 7200) =>
        new()
        {
            Path = $"/m/Film (2020)/{name}.{container}", Container = container, Width = height * 16 / 9, Height = height, VideoCodec = codec, VideoRange = range,
            Bitrate = bitrate, DurationSeconds = duration, AudioCodec = "aac",
            Streams = [new MediaStream { Index = 0, Type = StreamType.Video, Codec = codec, Profile = "Main", Width = height * 16 / 9, Height = height },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true }],
        };

    private static readonly DeviceProfile AppleTv = new()
    {
        Containers = ["mp4", "mov"], VideoCodecs = ["h264", "hevc"], AudioCodecs = ["aac"], MaxHeight = 2160, Hdr = true, Hdr10 = true, Fmp4 = true,
    };

    private static readonly DeviceProfile Phone1080 = new()
    {
        Containers = ["mp4", "mov", "matroska,webm", "mkv"], VideoCodecs = ["h264", "hevc"], AudioCodecs = ["aac"], MaxHeight = 1440,
    };

    private static readonly DeviceProfile Browser = new()
    {
        Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"], MaxHeight = 2160,
    };

    private static MediaFile? Pick(DeviceProfile device, VersionChoice? choice, long? maxBitrate, params MediaFile[] files)
    {
        var options = new PlaybackOptions(MaxBitrate: maxBitrate);
        return VersionPicker.Pick(files, choice, device.MaxHeight,
            f => PlaybackDecider.TierOf(PlaybackDecider.Decide(f, device, [], options), f, device, options));
    }

    private static VersionChoice Choice(MediaFile file) =>
        new() { ProfileId = "p", MediaItemId = Guid.NewGuid(), MediaFileId = file.Id, Label = VersionRules.Label(file), Edition = VersionRules.Edition(file) };

    private readonly MediaFile _uhd = File("Film 4K", 2160, bitrate: 20_000_000);
    private readonly MediaFile _hd = File("Film 1080p", 1080);

    [Fact]
    public void ALimitedPhone_GetsThe1080pCopy_RatherThanA4KConversion() => Assert.Same(_hd, Pick(Phone1080, null, null, _uhd, _hd));

    [Fact]
    public void AnAppleTv_Gets4KRepackaged_NotAn1080pThatPlaysAsItIs()
    {
        var mp4 = File("Film 1080p", 1080, "mp4");
        Assert.Same(_uhd, Pick(AppleTv, null, null, _uhd, mp4));
    }

    [Fact]
    public void BetweenAlikePictures_TheOneThatPlaysAsItIsWins()
    {
        var mkv = File("Film a", 2160);
        var mp4 = File("Film b", 2160, "mp4", bitrate: 1_000_000);
        Assert.Same(mp4, Pick(AppleTv, null, null, mkv, mp4));
    }

    [Fact]
    public void ASlowConnection_GetsTheCopyThatFits() => Assert.Same(_hd, Pick(Phone1080 with { MaxHeight = 2160 }, null, 10_000_000, _uhd, _hd));

    [Fact]
    public void WhenEverythingNeedsConverting_TheSmallestSourceThatFillsTheScreenIsUsed()
    {
        var sd = File("Film SD", 480);
        Assert.Same(_hd, Pick(Browser with { MaxHeight = 1080 }, null, null, _uhd, _hd, sd));
    }

    [Fact]
    public void WhenEverythingNeedsConverting_AndNothingFills_TheBestIsUsed()
    {
        var sd = File("Film SD", 480);
        Assert.Same(_hd, Pick(Browser, null, null, _hd, sd));
    }

    [Fact]
    public void TheAutomaticPick_NeverChangesCut()
    {
        // The Director's Cut is the only one that plays directly here, but the main cut is the 4K one.
        var directors = File("Film (2020) {edition-Director's Cut}", 1080, duration: 9000);
        Assert.Same(_uhd, Pick(Phone1080, null, null, _uhd, directors));
    }

    [Fact]
    public void AChoice_IsHonoured()
    {
        Assert.Same(_uhd, Pick(AppleTv, Choice(_uhd), null, _uhd, File("Film 1080p", 1080, "mp4")));
        var mp4 = File("Film 1080p", 1080, "mp4");
        Assert.Same(mp4, Pick(AppleTv, Choice(mp4), null, _uhd, mp4));
    }

    [Fact]
    public void AChoice_GivesWay_WhenItNeedsConvertingAndAnotherVersionDoesNot() =>
        Assert.Same(_hd, Pick(Phone1080, Choice(_uhd), null, _uhd, _hd));

    [Fact]
    public void AChoice_StandsWhenEverythingNeedsConverting()
    {
        var hdMkv = File("Film 1080p", 1080);
        Assert.Same(_uhd, Pick(Browser, Choice(_uhd), null, _uhd, hdMkv));
    }

    [Fact]
    public void AChoiceOfADifferentCut_StandsEvenIfItNeedsConverting()
    {
        var directors = File("Film (2020) {edition-Director's Cut}", 2160, duration: 9000);
        Assert.Same(directors, Pick(Phone1080, Choice(directors), null, _hd, directors));
    }

    [Fact]
    public void AChoicesReplacedFile_IsFoundByLabelAndEdition()
    {
        var old = Choice(File("Old", 2160));
        var replaced = File("Upgrade", 2160, "mp4");
        Assert.Same(replaced, Pick(AppleTv, old, null, _hd, replaced));
    }

    [Fact]
    public void AChoiceWithNothingLeftOfIt_IsIgnored()
    {
        var old = Choice(File("Old", 720));
        Assert.Same(_uhd, Pick(AppleTv, old, null, _hd, _uhd));
    }

    [Fact]
    public void OneFile_IsNotEvenAskedAbout()
    {
        var file = File("Film", 1080);
        Assert.Same(file, VersionPicker.Pick([file], null, 1080, _ => throw new InvalidOperationException()));
        Assert.Null(VersionPicker.Pick([], null, 1080, _ => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(VideoRange.Hdr10, true)]
    [InlineData(VideoRange.Hlg, false)] // This display's Hlg isn't said.
    public void Repackaging_NeedsADisplayShowingTheFilesPicture(VideoRange range, bool repackaged)
    {
        var file = File("Film", 2160, range: range);
        var options = new PlaybackOptions();
        var tier = PlaybackDecider.TierOf(PlaybackDecider.Decide(file, AppleTv, [], options), file, AppleTv, options);
        Assert.Equal(repackaged ? PlaybackTier.Repackaged : PlaybackTier.Converted, tier);
    }
}
