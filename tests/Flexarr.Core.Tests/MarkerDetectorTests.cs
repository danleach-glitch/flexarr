using Flexarr.Core.Media;
using Flexarr.Streaming.Markers;

namespace Flexarr.Core.Tests;

public class MarkerDetectorTests
{
    [Fact]
    public void FromChapters_RecognisesIntroRecapAndCredits()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0.000000","end_time":"45.000000","tags":{"title":"Previously On"}},
              {"start_time":"45.000000","end_time":"105.500000","tags":{"title":"Opening"}},
              {"start_time":"105.500000","end_time":"2500.000000","tags":{"title":"Chapter 2"}},
              {"start_time":"2500.000000","end_time":"2640.000000","tags":{"title":"End Credits"}}
            ]}
            """;

        var markers = MarkerDetector.FromChapters(json);

        Assert.Equal(
            [(MarkerType.Recap, 0d, 45d), (MarkerType.Intro, 45d, 105.5d), (MarkerType.Credits, 2500d, 2640d)],
            markers.Select(m => (m.Type, m.Start, m.End)));
    }

    /// <summary>Peacemaker S2E6: a chapter called "Credits" at 199–300 s of a 36-minute episode is the opening titles.</summary>
    [Fact]
    public void FromChapters_TakesEarlyCreditsAsTheOpeningTitles_AnIntro()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0","end_time":"198.907","tags":{"title":"Chapter 1"}},
              {"start_time":"198.907","end_time":"299.758","tags":{"title":"Credits"}},
              {"start_time":"299.758","end_time":"2154.304","tags":{"title":"Chapter 2"}}]}
            """;

        var marker = Assert.Single(MarkerDetector.FromChapters(json));

        Assert.Equal(MarkerType.Intro, marker.Type);
        Assert.Equal(198.907, marker.Start, 3);
        // At the end, credits are credits.
        var ending = Assert.Single(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"2400","tags":{"title":"Chapter 1"}},{"start_time":"2400","end_time":"2500","tags":{"title":"Credits"}}]}"""));
        Assert.Equal(MarkerType.Credits, ending.Type);
    }

    /// <summary>A two-hour film whose "Ending" chapter starts at 75%: that's the climax, with 18 minutes still to go.</summary>
    [Fact]
    public void FromChapters_IgnoresCreditsTitlesWellBeforeTheEnd()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0","end_time":"5400","tags":{"title":"Chapter 1"}},
              {"start_time":"5400","end_time":"6120","tags":{"title":"Ending"}},
              {"start_time":"6120","end_time":"7200","tags":{"title":"Chapter 3"}}]}
            """;

        Assert.Empty(MarkerDetector.FromChapters(json));
        // Credits followed by a short post-credits scene are still credits.
        var credits = Assert.Single(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"6600","tags":{"title":"Chapter 1"}},{"start_time":"6600","end_time":"7020","tags":{"title":"Credits"}},{"start_time":"7020","end_time":"7200","tags":{"title":"Post-credits"}}]}"""));
        Assert.Equal(MarkerType.Credits, credits.Type);
    }

    [Fact]
    public void FromChapters_IgnoresNumberedChapters() =>
        Assert.Empty(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"600","tags":{"title":"Chapter 1"}}]}"""));

    [Fact]
    public async Task ASeason_GivesWayBeforeEachEpisodesSoundIsDecoded_AndDecodesItBelowNormalPriority()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("flexarr-markers-").FullName;
        try
        {
            // Stand in for ffprobe (no chapters) and ffmpeg: each decode is noted, and gives back a second of silence.
            // The first also notes how nice it's being, once it has had time to be put in the background.
            var log = Path.Combine(folder, "log");
            var nice = Path.Combine(folder, "nice");
            var ffprobe = Path.Combine(folder, "fake-ffprobe");
            var ffmpeg = Path.Combine(folder, "fake-ffmpeg");
            File.WriteAllText(ffprobe, """
                #!/bin/sh
                echo '{"chapters":[]}'
                """);
            File.WriteAllText(ffmpeg, $"""
                #!/bin/sh
                if [ ! -e '{nice}' ]; then sleep 1; ps -o nice= -p $$ > '{nice}' 2>/dev/null; fi
                case "$*" in *-sseof*) echo "end $*" >> '{log}';; *) echo "start $*" >> '{log}';; esac
                head -c 22050 /dev/zero
                """);
            File.SetUnixFileMode(ffprobe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var detector = new MarkerDetector(ffmpeg, ffprobe);
            // The second is too short to have its end compared: only its start is decoded.
            MediaFile[] episodes =
            [
                new() { Id = Guid.NewGuid(), Path = "/shows/One.mkv", DurationSeconds = 1300 },
                new() { Id = Guid.NewGuid(), Path = "/shows/Two.mkv", DurationSeconds = 500 },
                new() { Id = Guid.NewGuid(), Path = "/shows/Three.mkv", DurationSeconds = 1300 },
            ];

            var found = await detector.DetectSeasonAsync(episodes, CancellationToken.None, giveWay: _ =>
            {
                File.AppendAllText(log, "gave way\n");
                return Task.CompletedTask;
            });

            string[] order = [.. File.ReadAllLines(log).Select(line => line.Split(' ')[0] == "gave" ? "gave way" : $"{line.Split(' ')[0]} {Path.GetFileName(line.Split(' ').First(word => word.EndsWith(".mkv", StringComparison.Ordinal)))}")];
            Assert.Equal(
                ["gave way", "start One.mkv", "end One.mkv", "gave way", "start Two.mkv", "gave way", "start Three.mkv", "end Three.mkv"],
                order);
            // Where ps can say (not every container has one that can), ffmpeg ran nicer than this process does.
            if (int.TryParse(File.ReadAllText(nice).Trim(), out var niceness))
            {
                Assert.True(niceness > 0, $"ffmpeg ran at niceness {niceness}");
            }

            // Waiting changes nothing about what's found: the same as without.
            File.Delete(log);
            var straight = await detector.DetectSeasonAsync(episodes, CancellationToken.None);
            Assert.Equal(found.Keys.Order(), straight.Keys.Order());
            Assert.All(episodes, e => Assert.Equal(found[e.Id], straight[e.Id]));
            Assert.Equal(5, File.ReadAllLines(log).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
