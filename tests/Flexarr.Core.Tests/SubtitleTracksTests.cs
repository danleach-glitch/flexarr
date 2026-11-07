using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class SubtitleTracksTests
{
    private static MediaFile File(bool? closedCaptions, params MediaStream[] streams) =>
        new() { Path = "/nowhere/Film (2020).mkv", ClosedCaptions = closedCaptions, Streams = [.. streams] };

    private static MediaStream Stream(int index, StreamType type, string codec, string? language = null) =>
        new() { Index = index, Type = type, Codec = codec, Language = language };

    [Fact]
    public void CaptionsInTheVideo_AreATextTrack_InTheProgrammesLanguage()
    {
        var tracks = Subtitles.For(File(true, Stream(0, StreamType.Video, "h264"), Stream(1, StreamType.Audio, "ac3", "eng")));

        var captions = Assert.Single(tracks);
        Assert.Equal(("cc0", 0, "eng", "CC"), (captions.Key, captions.StreamIndex, captions.Language, captions.Title));
        Assert.True(captions.InVideo);
        Assert.True(captions.IsText);
    }

    [Fact]
    public void NoCaptionsTrack_WhenTheVideoHasNoneOrHasntBeenChecked()
    {
        Assert.Empty(Subtitles.For(File(false, Stream(0, StreamType.Video, "h264"))));
        Assert.Empty(Subtitles.For(File(null, Stream(0, StreamType.Video, "h264"))));
    }

    [Fact]
    public void Teletext_IsTextOnlyWhenThisServerCanReadIt()
    {
        var file = File(false, Stream(0, StreamType.Video, "mpeg2video"), Stream(1, StreamType.Subtitle, "dvb_teletext", "deu"));

        Assert.False(Assert.Single(Subtitles.For(file)).IsText);
        Assert.True(Assert.Single(Subtitles.For(file, teletextReadable: true)).IsText);
    }

    [Theory]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("subrip", false)]
    public void StyledTracks_AreAssAndSsa(string codec, bool styled) =>
        Assert.Equal(styled, Assert.Single(Subtitles.For(File(false, Stream(2, StreamType.Subtitle, codec)))).IsStyled);

    [Theory]
    [InlineData(100, true)]
    [InlineData(888, true)]
    [InlineData(899, true)]
    [InlineData(99, false)]
    [InlineData(900, false)]
    [InlineData(1888, false)]
    [InlineData(0, false)]
    public void TeletextPages_AreThreeDigits_FromMagazineOneToEight(int page, bool valid) =>
        Assert.Equal(valid, Subtitles.IsTeletextPage(page));

    [Fact]
    public void Teletext_ReadsTheSubtitlePages_UnlessAPageIsChosen()
    {
        Assert.Equal(["-txt_format", "text", "-txt_page", "subtitle"], Subtitles.TeletextOptions());
        Assert.Equal(["-txt_format", "text", "-txt_page", "888"], Subtitles.TeletextOptions(888));
    }

    [Fact]
    public async Task AChosenTeletextPage_IsReadOnItsOwn_AndKeptApartFromTheUsualSubtitles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("flexarr-teletext-").FullName;
        try
        {
            // Stands in for ffmpeg: notes what it was asked, and writes the WebVTT it was asked for.
            var ffmpeg = Path.Combine(cache, "fake-ffmpeg");
            var asked = Path.Combine(cache, "asked");
            System.IO.File.WriteAllText(ffmpeg, $"""
                #!/bin/sh
                echo "$*" >> '{asked}'
                for last; do :; done
                printf 'WEBVTT\n' > "$last"
                """);
            System.IO.File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new TranscodeSettings { CacheDirectory = cache, FfmpegPath = ffmpeg };
            var file = File(false, Stream(0, StreamType.Video, "mpeg2video"), Stream(3, StreamType.Subtitle, "dvb_teletext", "eng"));
            var track = Assert.Single(Subtitles.For(file, teletextReadable: true));

            var path = await Subtitles.TeletextPageToWebVttAsync(file, track, 888, settings, CancellationToken.None);

            Assert.NotNull(path);
            Assert.NotEqual(Subtitles.CachePath(file, track, settings), path);
            Assert.Contains("-txt_page 888 -i /nowhere/Film (2020).mkv", System.IO.File.ReadAllText(asked), StringComparison.Ordinal);
            Assert.Contains("-map 0:3 -c:s webvtt", System.IO.File.ReadAllText(asked), StringComparison.Ordinal);

            // Asked again, it's the copy already made.
            Assert.Equal(path, await Subtitles.TeletextPageToWebVttAsync(file, track, 888, settings, CancellationToken.None));
            Assert.Single(System.IO.File.ReadAllLines(asked));

            await Assert.ThrowsAsync<ArgumentException>(() => Subtitles.TeletextPageToWebVttAsync(file, track, 999, settings, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task CaptionsAreDecodedBelowNormalPriority_SoTheyGiveWayToTheStreamBeingWatched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("flexarr-captions-").FullName;
        try
        {
            // Stands in for ffmpeg: once it has had time to be put in the background, notes how nice it's being, and
            // writes the WebVTT it was asked for.
            var ffmpeg = Path.Combine(cache, "fake-ffmpeg");
            var nice = Path.Combine(cache, "nice");
            System.IO.File.WriteAllText(ffmpeg, $"""
                #!/bin/sh
                case "$*" in *"filter=movie"*) exit 0;; esac
                sleep 1
                ps -o nice= -p $$ > '{nice}' 2>/dev/null
                for last; do :; done
                printf 'WEBVTT\n' > "$last"
                """);
            System.IO.File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new TranscodeSettings { CacheDirectory = cache, FfmpegPath = ffmpeg };
            var file = File(true, Stream(0, StreamType.Video, "h264"), Stream(1, StreamType.Audio, "ac3", "eng"));
            var track = Assert.Single(Subtitles.For(file));

            var converted = await Subtitles.ToWebVttAsync(file, [track], settings, CancellationToken.None);

            Assert.Equal(Subtitles.CachePath(file, track, settings), converted[track.Key]);
            // Where ps can say (not every container has one that can), it ran nicer than this process does.
            if (int.TryParse(System.IO.File.ReadAllText(nice).Trim(), out var niceness))
            {
                Assert.True(niceness > 0, $"ffmpeg ran at niceness {niceness}");
            }
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task ATrackThatDidntConvert_IsLeftAloneAsPlaybackStarts_UntilItsAskedForOrTheFileChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("flexarr-failed-").FullName;
        try
        {
            // Stands in for ffmpeg: notes each run, and fails until it's told to work.
            var ffmpeg = Path.Combine(cache, "fake-ffmpeg");
            var asked = Path.Combine(cache, "asked");
            var works = Path.Combine(cache, "works");
            System.IO.File.WriteAllText(ffmpeg, $"""
                #!/bin/sh
                echo "$*" >> '{asked}'
                [ -e '{works}' ] || exit 1
                for arg; do case "$arg" in *.tmp) printf 'WEBVTT\n' > "$arg";; esac; done
                """);
            System.IO.File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new TranscodeSettings { CacheDirectory = cache, FfmpegPath = ffmpeg };
            var film = Path.Combine(cache, "Film (2020).mkv");
            System.IO.File.WriteAllText(film, "not really a film");
            var file = new MediaFile { Path = film, Streams = [Stream(0, StreamType.Video, "h264"), Stream(2, StreamType.Subtitle, "subrip", "eng"), Stream(3, StreamType.Subtitle, "subrip", "fre")] };
            var tracks = Subtitles.For(file);
            Assert.Equal(["e2", "e3"], tracks.Select(t => t.Key));
            Assert.DoesNotContain(tracks, t => Subtitles.Failed(file, t, settings));

            // The read fails: noted against both tracks, and playback starting again doesn't read the file again.
            Assert.Empty(await Subtitles.ToWebVttAsync(file, tracks, settings, CancellationToken.None, retryFailed: false));
            Assert.All(tracks, t => Assert.True(Subtitles.Failed(file, t, settings)));
            Assert.Empty(await Subtitles.ToWebVttAsync(file, tracks, settings, CancellationToken.None, retryFailed: false));
            Assert.Single(System.IO.File.ReadAllLines(asked));

            // Asked for, a track is tried again; and once it converts it's no longer marked.
            Assert.Empty(await Subtitles.ToWebVttAsync(file, [tracks[0]], settings, CancellationToken.None));
            Assert.Equal(2, System.IO.File.ReadAllLines(asked).Length);
            System.IO.File.WriteAllText(works, "");
            var converted = await Subtitles.ToWebVttAsync(file, [tracks[0]], settings, CancellationToken.None);
            Assert.Equal(Subtitles.CachePath(file, tracks[0], settings), converted["e2"]);
            Assert.False(Subtitles.Failed(file, tracks[0], settings));
            Assert.True(Subtitles.Failed(file, tracks[1], settings));

            // A changed file is a new file: everything about it is tried afresh.
            System.IO.File.SetLastWriteTimeUtc(film, DateTime.UtcNow.AddDays(1));
            Assert.False(Subtitles.Failed(file, tracks[1], settings));
            Assert.Equal(["e2", "e3"], (await Subtitles.ToWebVttAsync(file, tracks, settings, CancellationToken.None, retryFailed: false)).Keys.Order());
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task AReadThatRunsOutOfTime_IsNotedToo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("flexarr-slow-").FullName;
        try
        {
            // Stands in for ffmpeg reading a file from a share that's too slow.
            var ffmpeg = Path.Combine(cache, "fake-ffmpeg");
            System.IO.File.WriteAllText(ffmpeg, """
                #!/bin/sh
                exec sleep 30
                """);
            System.IO.File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new TranscodeSettings { CacheDirectory = cache, FfmpegPath = ffmpeg };
            var file = File(false, Stream(0, StreamType.Video, "h264"), Stream(2, StreamType.Subtitle, "subrip", "eng"));
            var track = Assert.Single(Subtitles.For(file));

            using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Subtitles.ToWebVttAsync(file, [track], settings, limit.Token, retryFailed: false));

            Assert.True(Subtitles.Failed(file, track, settings));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
