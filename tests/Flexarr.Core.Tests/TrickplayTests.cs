using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public sealed class TrickplayTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-trickplay-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task AFailure_IsRememberedForThatVersionOfTheFile_UntilPreviewsAreMade()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fileId = Guid.NewGuid();
        var failing = Script("failing-ffmpeg", "exit 1");
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = failing, HardwareAcceleration = HardwareAcceleration.None };
        Assert.Null(await Trickplay.GenerateAsync("/film.mkv", fileId, 1920, 1080, hdr: false, tenBit: false, settings, CancellationToken.None));

        Trickplay.NoteFailure(_dir, fileId, "1|100");

        Assert.True(Trickplay.Failed(_dir, fileId, "1|100"));
        Assert.False(Trickplay.Failed(_dir, fileId, "2|100")); // the file changed: worth another go
        Assert.False(Trickplay.Failed(_dir, Guid.NewGuid(), "1|100"));

        // Made after all (the file replaced, say): the failure is forgotten.
        var working = Script("ffmpeg", "for last; do :; done\necho x > \"$(dirname \"$last\")/sheet001.jpg\"");
        Assert.NotNull(await Trickplay.GenerateAsync("/film.mkv", fileId, 1920, 1080, hdr: false, tenBit: false, settings with { FfmpegPath = working }, CancellationToken.None));
        Assert.False(Trickplay.Failed(_dir, fileId, "1|100"));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
