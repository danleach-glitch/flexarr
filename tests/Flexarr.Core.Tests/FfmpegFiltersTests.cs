using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public sealed class FfmpegFiltersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-filters-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("   format_opts       <dictionary> ..FVA...... set format options for the opened file", true)]
    [InlineData("   dec_threads       <int>        ..FVA...... set the number of threads for decoding", false)]
    public void TheMovieFiltersFormatOptions_AreFoundInItsHelp(string line, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ffmpeg = Path.Combine(_dir, $"ffmpeg-{expected}");
        File.WriteAllText(ffmpeg, $"#!/bin/sh\necho 'movie AVOptions:'\necho '   filename          <string>     ..FVA......'\necho '{line}'\n");
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal(expected, FfmpegFilters.MovieTakesFormatOptions(ffmpeg));
    }

    [Fact]
    public void AnFfmpegThatCantBeAsked_IsTakenNotToHaveThem() =>
        Assert.False(FfmpegFilters.MovieTakesFormatOptions(Path.Combine(_dir, "missing")));
}
