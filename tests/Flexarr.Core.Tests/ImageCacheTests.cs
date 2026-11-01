using Flexarr.Metadata;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class ImageCacheTests
{
    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w500/abc123.jpg", "https://image.tmdb.org/t/p/original/abc123.jpg")] // one size folder, so
    [InlineData("https://image.tmdb.org/t/p/w1280/abc123.jpg", "https://image.tmdb.org/t/p/original/abc123.jpg")] // one cache entry a width
    [InlineData("https://image.tmdb.org/t/p/original/abc123.jpg?n=1", "https://image.tmdb.org/t/p/original/abc123.jpg")]
    [InlineData("https://IMAGE.tmdb.org/t/p/w92/abc123.png#x", "https://image.tmdb.org/t/p/original/abc123.png")]
    [InlineData("https://image.tmdb.org/t/p/w500/x/abc123.jpg", null)]
    [InlineData("https://image.tmdb.org/t/p/abc123.jpg", null)]
    [InlineData("https://artworks.thetvdb.com/banners/v4/series/1/posters/a.jpg", "https://artworks.thetvdb.com/banners/v4/series/1/posters/a.jpg")]
    [InlineData("https://static.tvmaze.com/uploads/images/original_untouched/1/2.jpg", "https://static.tvmaze.com/uploads/images/original_untouched/1/2.jpg")]
    [InlineData("https://assets.fanart.tv/fanart/tv/1/clearlogo/a.png", "https://assets.fanart.tv/fanart/tv/1/clearlogo/a.png")]
    [InlineData("https://m.media-amazon.com/images/I/51ef+s6y42L._SL500_.jpg", "https://m.media-amazon.com/images/I/51ef+s6y42L._SL500_.jpg")]
    [InlineData("https://m.media-amazon.com/images/I/51AbcDef._SL500_.jpg", "https://m.media-amazon.com/images/I/51AbcDef._SL500_.jpg")]
    [InlineData("https://covers.openlibrary.org/b/id/12345-L.jpg", "https://covers.openlibrary.org/b/id/12345-L.jpg")]
    [InlineData("https://covers.openlibrary.org/a/olid/OL1A-L.jpg", null)] // authors' photos aren't book covers
    [InlineData("http://image.tmdb.org/t/p/w500/abc123.jpg", null)] // not https
    [InlineData("https://image.tmdb.org:8443/t/p/w500/abc123.jpg", null)]
    [InlineData("https://user@image.tmdb.org/t/p/w500/abc123.jpg", null)]
    [InlineData("https://evil.example/t/p/w500/abc123.jpg", null)]
    [InlineData("https://image.tmdb.org/3/movie/550", null)] // the API, not an image
    [InlineData("https://image.tmdb.org/t/p/w500/abc123.html", null)]
    [InlineData("https://assets.fanart.tv/fanart/../secret.jpg", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("not a url", null)]
    public void TrustedRemote_AllowsOnlyKnownArtwork_WithoutQueryStrings(string url, string? expected) =>
        Assert.Equal(expected, ArtworkUrl.TrustedRemote(url));

    [Fact]
    public void MetadataArtwork_MustBeHttps_AndOnlyTheScannerUsesLocalFiles()
    {
        Assert.True(ArtworkUrl.IsHttps("https://image.tmdb.org/t/p/original/a.jpg"));
        Assert.False(ArtworkUrl.IsHttps("http://image.tmdb.org/t/p/original/a.jpg"));
        Assert.False(ArtworkUrl.IsHttps("file:///Volumes/Media/Music/cover.jpg"));
        Assert.True(ArtworkUrl.IsLocal("file:///Volumes/Media/Music/cover.jpg"));
        Assert.True(ArtworkUrl.IsLocal("embedded:///Volumes/Media/Music/01.flac"));
        Assert.False(ArtworkUrl.IsLocal("https://image.tmdb.org/t/p/original/a.jpg"));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpeg_pipe")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "png_pipe")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "webp_pipe")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x23, 0x45, 0x58, 0x54, 0x4D, 0x33, 0x55 }, null)] // "#EXTM3U": a playlist named .jpg
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, null)] // "<html"
    [InlineData(new byte[] { }, null)]
    public void InputFormat_ComesFromTheContent(byte[] header, string? expected) =>
        Assert.Equal(expected, ImageCache.InputFormat(header));

    [Theory]
    [InlineData("https://thetvdb.com/banners/a.jpg", "https://artworks.thetvdb.com/banners/a.jpg", true)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://image.tmdb.org/t/p/original/a.jpg", true)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "http://image.tmdb.org/t/p/w500/a.jpg", false)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://evil.example/a.jpg", false)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://127.0.0.1/a.jpg", false)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://image.tmdb.org:8080/a.jpg", false)]
    public void Redirects_AreOnlyFollowedToHttpsOnTheSameSite(string from, string to, bool followed) =>
        Assert.Equal(followed, ImageCache.SameSite(new Uri(from), new Uri(to)));

    [Theory]
    [InlineData("https://covers.openlibrary.org/a/olid/OL1A-L.jpg", "https://archive.org/download/x/1-L.jpg", true)]
    [InlineData("https://covers.openlibrary.org/b/id/1-L.jpg", "https://ia800100.us.archive.org/view_archive.php?file=1-L.jpg", true)]
    [InlineData("https://covers.openlibrary.org/b/id/1-L.jpg", "http://archive.org/download/x/1-L.jpg", false)]
    [InlineData("https://covers.openlibrary.org/b/id/1-L.jpg", "https://evil.example/1-L.jpg", false)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", "https://archive.org/download/x/a.jpg", false)]
    [InlineData("https://openlibrary.org/a.jpg", "https://archive.org/download/x/a.jpg", false)]
    public void OpenLibraryCovers_MayGoToTheInternetArchive_AndNowhereElse(string from, string to, bool followed) =>
        Assert.Equal(followed, ImageCache.MayFollow(new Uri(from), new Uri(to)));

    [Fact]
    public void Trim_RemovesTheLeastRecentlyUsed_UntilUnderTheCap()
    {
        var directory = Directory.CreateTempSubdirectory("flexarr-images-").FullName;
        try
        {
            var now = DateTime.UtcNow;
            string Write(string name, int ageDays)
            {
                var path = Path.Combine(directory, name[..2], name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[1000]);
                File.SetLastWriteTimeUtc(path, now.AddDays(-ageDays));
                return path;
            }

            var oldest = Write("aa_w500.jpg", 30);
            var older = Write("ab_w500.png", 20);
            var recent = Write("ac_w500.jpg", 1);
            var newest = Write("ad_w500.jpg", 0);
            var downloading = Write("af_w500.jpg.0123.download", 0); // still being written, and counted
            var leftResize = Write("ae_w500.0123.tmp.jpg", 1); // left by a crash
            var leftDownload = Write("ag_w500.jpg.4567.download", 1);

            var removed = ImageCache.Trim(directory, 3000); // 5000 bytes: trim to 2700

            Assert.Equal(5, removed);
            Assert.False(File.Exists(oldest) || File.Exists(older) || File.Exists(recent));
            Assert.False(File.Exists(leftResize) || File.Exists(leftDownload));
            Assert.True(File.Exists(newest) && File.Exists(downloading));
            Assert.Equal(0, ImageCache.Trim(directory, 3000));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("/m/Movie.en.srt", new[] { "-f", "srt" })]
    [InlineData("/m/Movie.en.SRT", new[] { "-f", "srt" })]
    [InlineData("/m/Movie.vtt", new[] { "-f", "webvtt" })]
    [InlineData("/m/Movie.ass", new[] { "-f", "ass" })]
    [InlineData("/m/Movie.ssa", new[] { "-f", "ass" })]
    [InlineData("/m/Movie.sub", new string[0])]
    public void Sidecars_AreReadAsTheFormatTheirNameSays(string path, string[] expected) =>
        Assert.Equal(expected, Subtitles.SidecarFormat(path));
}
