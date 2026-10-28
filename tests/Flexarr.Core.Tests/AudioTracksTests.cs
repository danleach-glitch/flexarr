using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class AudioTracksTests
{
    private static MediaStream Audio(int index, string? language, string? title = null, bool isDefault = false) =>
        new() { Index = index, Type = StreamType.Audio, Codec = "aac", Language = language, Title = title, IsDefault = isDefault };

    [Fact]
    public void Commentary_AndDescription_AreSkipped()
    {
        var streams = new[] { Audio(1, "eng", "Director's Commentary"), Audio(2, "eng", "Audio Description"), Audio(3, "eng", "English 5.1") };

        Assert.Equal(3, AudioTracks.InLanguage(streams, "eng")?.Index);
    }

    [Fact]
    public void TheDefault_WinsAmongTheMainTracks()
    {
        var streams = new[] { Audio(1, "eng", "Stereo"), Audio(2, "eng", "Surround", isDefault: true), Audio(3, "fre") };

        Assert.Equal(2, AudioTracks.InLanguage(streams, "eng")?.Index);
    }

    [Fact]
    public void OnlyExtras_StillPlaySomethingInTheLanguage() =>
        Assert.Equal(4, AudioTracks.InLanguage([Audio(1, "fre"), Audio(4, "eng", "Commentary")], "eng")?.Index);

    [Fact]
    public void NoPreference_OrNoMatch_IsNull()
    {
        Assert.Null(AudioTracks.InLanguage([Audio(1, "eng")], null));
        Assert.Null(AudioTracks.InLanguage([Audio(1, "eng")], "jpn"));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fre", "fr")]
    [InlineData("ger", "de")]
    [InlineData("jpn", "ja")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("en", "en")]
    [InlineData(null, "und")]
    [InlineData("und", "und")]
    [InlineData("qaa", "qaa")]
    public void Tag_IsBcp47(string? code, string expected) => Assert.Equal(expected, Languages.Tag(code));
}
