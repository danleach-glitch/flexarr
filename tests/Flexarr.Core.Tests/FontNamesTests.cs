using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

/// <summary>Which attached fonts a styled subtitle track uses (audit perf-66).</summary>
public sealed class FontNamesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-fontnames-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A font with only a name table: a family (Windows, UTF-16) and a full name (Mac, Roman).</summary>
    internal static byte[] Font(string family, string full, uint version = 0x00010000)
    {
        var familyBytes = Encoding.BigEndianUnicode.GetBytes(family);
        var fullBytes = Encoding.Latin1.GetBytes(full);
        var name = new byte[6 + (2 * 12) + familyBytes.Length + fullBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(4), 6 + (2 * 12));
        void Record(int at, ushort platform, ushort encoding, ushort id, int length, int offset)
        {
            var r = name.AsSpan(6 + (12 * at));
            BinaryPrimitives.WriteUInt16BigEndian(r, platform);
            BinaryPrimitives.WriteUInt16BigEndian(r[2..], encoding);
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], id);
            BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)length);
            BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)offset);
        }

        Record(0, 3, 1, 1, familyBytes.Length, 0);
        Record(1, 1, 0, 4, fullBytes.Length, familyBytes.Length);
        familyBytes.CopyTo(name, 30);
        fullBytes.CopyTo(name, 30 + familyBytes.Length);

        var font = new byte[12 + 16 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(font, version);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4), 1);
        "name"u8.CopyTo(font.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(20), 28);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(24), (uint)name.Length);
        name.CopyTo(font, 28);
        return font;
    }

    [Fact]
    public void A_fonts_family_and_full_names_are_read()
    {
        Assert.Equal(["Sign Font", "Sign Font Bold"], FontNames.Read(Font("Sign Font", "Sign Font Bold"))!.Order());
        Assert.Equal(["Kana", "Kana Regular"], FontNames.Read(Font("Kana", "Kana Regular", 0x4F54544F))!.Order());
        Assert.Contains("SIGN FONT", FontNames.Read(Font("Sign Font", "x"))!);
    }

    [Fact]
    public void A_collection_gives_every_fonts_names()
    {
        var one = Font("First", "First Regular");
        var two = Font("Second", "Second Regular");
        // A collection's fonts point into the whole file: shift the second's name table to where it lands.
        var collection = new byte[12 + 8 + one.Length + two.Length];
        "ttcf"u8.CopyTo(collection);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(12), 20);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(16), (uint)(20 + one.Length));
        BinaryPrimitives.WriteUInt32BigEndian(one.AsSpan(20), (uint)(20 + 28));
        BinaryPrimitives.WriteUInt32BigEndian(two.AsSpan(20), (uint)(20 + one.Length + 28));
        one.CopyTo(collection, 20);
        two.CopyTo(collection, 20 + one.Length);

        Assert.Equal(["First", "First Regular", "Second", "Second Regular"], FontNames.Read(collection)!.Order());
    }

    [Fact]
    public void What_isnt_a_readable_font_gives_no_names()
    {
        Assert.Null(FontNames.Read("not really a font"u8));
        Assert.Null(FontNames.Read("wOF2 and some compressed bytes"u8));
        var cut = Font("Sign Font", "Sign Font Bold");
        Assert.Null(FontNames.Read(cut.AsSpan(0, 40)));
    }

    [Fact]
    public void A_script_names_its_styles_fonts_and_its_overrides()
    {
        const string script = """
            [Script Info]
            ScriptType: v4.00+

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour
            Style: Default,Gandhi Sans,40,&H00FFFFFF
            Style: Vertical,@Kana Mincho,40,&H00FFFFFF

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.50,0:00:02.00,Default,,0,0,0,,{\fnSign Font\b1}Hello, {\fn}there{\i1\fnOther Face}
            """;

        Assert.Equal(["Gandhi Sans", "Kana Mincho", "Other Face", "Sign Font"], FontNames.InScript(script).Order());
        // A Format line that puts Fontname elsewhere.
        Assert.Equal(["Moved"], FontNames.InScript("[V4+ Styles]\nFormat: Fontname, Name\nStyle: Moved,Default\n"));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_only_the_fonts_a_script_names_are_kept_and_unreadable_ones_too()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        var used = Path.Combine(_dir, "used.ttf");
        var other = Path.Combine(_dir, "other.ttf");
        var unreadable = Path.Combine(_dir, "mystery.ttf");
        await File.WriteAllBytesAsync(used, Font("Sign Font", "Sign Font Regular"));
        await File.WriteAllBytesAsync(other, Font("Unused Face", "Unused Face Regular"));
        await File.WriteAllTextAsync(unreadable, "not really a font");
        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-attach", used, "-attach", other, "-attach", unreadable, "-metadata:s:t", "mimetype=application/x-truetype-font", clip);
        var file = new MediaFile { Path = clip };
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg" };
        var fonts = await SubtitleFonts.ListAsync(file, "ffprobe", default);
        Assert.Equal(3, fonts.Count);

        var kept = await FontNames.UsedByAsync(file, fonts, "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Sign,sign font\n", settings, default);
        Assert.Equal(["mystery.ttf", "used.ttf"], kept.Select(f => f.Name).Order());

        // A script that names no font: every font, as without the filter.
        Assert.Equal(fonts, await FontNames.UsedByAsync(file, fonts, "[Script Info]\n", settings, default));
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
