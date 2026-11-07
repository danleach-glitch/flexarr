using System.Diagnostics;
using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

/// <summary>Runs the real ffmpeg on a generated clip. Without ffmpeg installed (CI), there's nothing to check.</summary>
public sealed class StyledSubtitleTests : IDisposable
{
    private const string Ass = """
        [Script Info]
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Sign,Sign Font,40,&H0000FFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.50,0:00:02.00,Sign,,0,0,0,,{\pos(320,40)}Café
        """;

    // Quotes, colons and brackets, which a filter graph would otherwise need escaped.
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-styled- it's: [a] test ").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenFfmpegIsInstalled_AnEmbeddedAssTrackIsKeptAsAuthored_AndItsFontsCanBeRead()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var (file, settings) = await ClipAsync();
        var track = Assert.Single(Subtitles.For(file), t => t.IsStyled);

        var converted = await Subtitles.ToWebVttAsync(file, [track], settings, default);

        Assert.Contains("Café", await File.ReadAllTextAsync(converted[track.Key]), StringComparison.Ordinal);
        var styled = await Subtitles.StyledAsync(file, track, settings, default);
        Assert.Contains(@"{\pos(320,40)}Café", styled, StringComparison.Ordinal);
        Assert.Contains("Style: Sign,Sign Font", styled, StringComparison.Ordinal);

        var font = Assert.Single(await SubtitleFonts.ListAsync(file, "ffprobe", default));
        Assert.Equal(("Sign Font.ttf", "font/ttf"), (font.Name, font.ContentType));
        var extracted = await SubtitleFonts.ExtractAsync(file, font, settings, default);
        Assert.Equal("not really a font", await File.ReadAllTextAsync(extracted!));

        // Asked again (a player wants every font at once): the list is remembered, not read with ffprobe each time.
        var missing = Path.Combine(_dir, "no-ffprobe-here");
        Assert.Equal(font, Assert.Single(await SubtitleFonts.ListAsync(file, missing, default)));
        // A file that changed is read again.
        File.SetLastWriteTimeUtc(file.Path, File.GetLastWriteTimeUtc(file.Path).AddMinutes(1));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => SubtitleFonts.ListAsync(file, missing, default));
        Assert.Equal(font, Assert.Single(await SubtitleFonts.ListAsync(file, "ffprobe", default)));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_EveryFontAskedForAtOnce_IsSavedWithOneFfmpegRun()
    {
        if (OperatingSystem.IsWindows() || !await HasFfmpegAsync())
        {
            return;
        }

        var (file, settings) = await ClipAsync(fonts: 3);
        var fonts = await SubtitleFonts.ListAsync(file, "ffprobe", default);
        Assert.Equal(["Sign Font.ttf", "Sign Font 2.ttf", "Sign Font 3.ttf"], fonts.Select(f => f.Name));

        // FFmpeg through a script that counts its runs.
        var runs = Path.Combine(_dir, "runs.log");
        var counting = Path.Combine(_dir, "counting-ffmpeg");
        await File.WriteAllTextAsync(counting, $"#!/bin/sh\necho run >> \"{runs}\"\nexec ffmpeg \"$@\"\n");
        File.SetUnixFileMode(counting, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var through = new TranscodeSettings { CacheDirectory = settings.CacheDirectory, FfmpegPath = counting };

        var saved = await Task.WhenAll(fonts.Select(f => SubtitleFonts.ExtractAsync(file, f, fonts, through, default)));

        Assert.Equal(["not really a font", "not really a font 2", "not really a font 3"], saved.Select(p => File.ReadAllText(p!)));
        Assert.Single(await File.ReadAllLinesAsync(runs));
        // Asked again, they come from the cache; a font asked for on its own is the same file.
        Assert.Equal(saved, await Task.WhenAll(fonts.Select(f => SubtitleFonts.ExtractAsync(file, f, fonts, through, default))));
        Assert.Equal(saved[1], await SubtitleFonts.ExtractAsync(file, fonts[1], through, default));
        Assert.Single(await File.ReadAllLinesAsync(runs));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "subtitles"), "*.tmp"));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_CaptionsAreReadFromTheVideo_WhateverTheFileIsCalled()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var (file, settings) = await ClipAsync();
        file.ClosedCaptions = true;
        var track = Assert.Single(Subtitles.For(file), t => t.InVideo);

        var converted = await Subtitles.ToWebVttAsync(file, [track], settings, default);

        // The clip has no caption data: an empty WebVTT, and no link left behind.
        Assert.StartsWith("WEBVTT", await File.ReadAllTextAsync(converted[track.Key]), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_dir, "subtitles")), f => Path.GetFileName(f).StartsWith("cc-", StringComparison.Ordinal));
    }

    private async Task<(MediaFile File, TranscodeSettings Settings)> ClipAsync(int fonts = 1)
    {
        var ass = Path.Combine(_dir, "signs.ass");
        await File.WriteAllTextAsync(ass, Ass);
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24", "-i", ass, "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:s", "copy" };
        for (var n = 1; n <= fonts; n++)
        {
            var font = Path.Combine(_dir, n == 1 ? "Sign Font.ttf" : $"Sign Font {n}.ttf");
            await File.WriteAllTextAsync(font, n == 1 ? "not really a font" : $"not really a font {n}");
            args.AddRange(["-attach", font]);
        }

        var clip = Path.Combine(_dir, "Clip: \"one\".mkv");
        args.AddRange(["-metadata:s:t", "mimetype=application/x-truetype-font", clip]);
        await RunAsync("ffmpeg", [.. args]);
        var file = new MediaFile
        {
            Path = clip,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264" },
                new MediaStream { Index = 1, Type = StreamType.Subtitle, Codec = "ass", Language = "eng" },
            ],
        };
        return (file, new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg" });
    }

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        _ = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{program} failed: {await errors}");
    }
}
