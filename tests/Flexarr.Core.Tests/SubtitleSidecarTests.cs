using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class SubtitleSidecarTests
{
    [Fact]
    public void BazarrsHearingImpairedTag_FollowsTheLanguage_WhileHiAloneIsHindi()
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-sidecars-").FullName;
        var video = Path.Combine(folder, "Film (2020).mkv");
        foreach (var name in new[] { "Film (2020).mkv", "Film (2020).en.hi.srt", "Film (2020).hi.srt", "Film (2020).fr.forced.srt" })
        {
            File.WriteAllText(Path.Combine(folder, name), "");
        }

        var tracks = Subtitles.For(new MediaFile { Path = video }).ToDictionary(t => Path.GetFileName(t.ExternalPath!));

        Assert.Equal(("en", "SDH"), (tracks["Film (2020).en.hi.srt"].Language, tracks["Film (2020).en.hi.srt"].Title));
        Assert.Equal(("hi", null), (tracks["Film (2020).hi.srt"].Language, tracks["Film (2020).hi.srt"].Title));
        Assert.True(tracks["Film (2020).fr.forced.srt"].IsForced);
        Directory.Delete(folder, recursive: true);
    }
}
