using System.Diagnostics;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <summary>Profile pictures, cropped square and shrunk to 320px JPEGs in the data folder.</summary>
public sealed partial class ProfileImages(IOptionsMonitor<FlexarrOptions> options, ILogger<ProfileImages> logger)
{
    public const long MaxUploadBytes = 15 * 1024 * 1024;
    private const int Size = 320;

    private string Directory => Path.Combine(options.CurrentValue.DataDirectory, "profile-images");

    /// <summary>Where a profile's picture lives. Ids are hex, so they're safe as file names.</summary>
    public string PathFor(string profileId) => Path.Combine(Directory, $"{Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(profileId))}.jpg");

    /// <summary>Converts any image ffmpeg can read; false if it isn't one.</summary>
    public Task<bool> SaveAsync(string profileId, Stream image, CancellationToken ct) => SaveSquareAsync(PathFor(profileId), Size, image, ct);

    /// <summary>
    /// Any image ffmpeg can read, cropped square and shrunk to a <paramref name="size"/>px JPEG at
    /// <paramref name="target"/> (people's photos use this too); false if it isn't an image.
    /// </summary>
    public async Task<bool> SaveSquareAsync(string target, int size, Stream image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(image);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var source = $"{target}.{Guid.NewGuid():N}.upload";
        var output = $"{target}.{Guid.NewGuid():N}.jpg";
        try
        {
            await using (var file = File.Create(source))
            {
                await image.CopyToAsync(file, ct);
            }

            // Read as the kind of image it is (HEIC and AVIF from phones through the mov demuxer), never as a
            // playlist or anything else that could make ffmpeg open other files.
            if (await InputFormatAsync(source, ct) is not { } format)
            {
                return false;
            }

            // Centre-crop to a square, then scale: avatars are always shown as circles.
            string[] args = ["-v", "error", "-y", "-protocol_whitelist", "file", "-f", format, "-i", source,
                "-vf", $"crop='min(iw,ih)':'min(iw,ih)',scale={size}:{size}", "-frames:v", "1", "-update", "1", "-q:v", "3", output];
            if (!await RunFfmpegAsync(args, ct) || !File.Exists(output))
            {
                return false;
            }

            File.Move(output, target, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    public void Delete(string profileId)
    {
        // Before any picture has been saved the folder doesn't exist, which File.Delete doesn't forgive.
        if (File.Exists(PathFor(profileId)))
        {
            File.Delete(PathFor(profileId));
        }
    }

    private static async Task<string?> InputFormatAsync(string path, CancellationToken ct)
    {
        var header = new byte[12];
        await using var file = File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        return Flexarr.Metadata.ImageCache.InputFormat(header.AsSpan(0, read))
            ?? (read == 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8) ? "mov" : null);
    }

    private async Task<bool> RunFfmpegAsync(string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.CurrentValue.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            LogConvertFailed($"{options.CurrentValue.FfmpegPath}: {ex.Message}");
            return false;
        }

        using var _ = process;
        string stderr;
        try
        {
            stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process doesn't stop it; a cancelled upload shouldn't leave ffmpeg running.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            LogConvertFailed(stderr.Trim());
        }

        return process.ExitCode == 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read a profile picture: {Error}")]
    private partial void LogConvertFailed(string error);
}
