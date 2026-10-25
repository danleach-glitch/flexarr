using System.Text;
using Flexarr.Core.Podcasts;

namespace Flexarr.Core.Tests;

public sealed class FeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:podcast="https://podcastindex.org/namespace/1.0"
             xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title> The History Hour </title>
            <link>https://example.com/history</link>
            <language>en-GB</language>
            <description>Short.</description>
            <itunes:summary>A longer &lt;b&gt;summary&lt;/b&gt; of the show.</itunes:summary>
            <itunes:author>Jane Smith</itunes:author>
            <itunes:image href="https://cdn.example.com/show.jpg"/>
            <itunes:explicit>no</itunes:explicit>
            <itunes:category text="History"/>
            <itunes:category text="Society &amp; Culture"><itunes:category text="Documentary"/></itunes:category>
            <itunes:new-feed-url>https://feeds.example.com/new</itunes:new-feed-url>
            <podcast:guid>9b024349-ccf0-5f69-a609-6b82873eab3c</podcast:guid>
            <item>
              <title>The Fall of Rome</title>
              <guid isPermaLink="false">ep-2</guid>
              <pubDate>Tue, 03 Sep 2024 10:00:00 +0100</pubDate>
              <content:encoded><![CDATA[<p>Part one.</p><p>With <a href="https://x">links</a> &amp; more.</p><ul><li>One</li><li>Two</li></ul>]]></content:encoded>
              <description>Plain description.</description>
              <enclosure url="https://cdn.example.com/ep2.mp3" length="12345678" type="audio/mpeg"/>
              <itunes:duration>1:02:03</itunes:duration>
              <itunes:season>2</itunes:season>
              <itunes:episode>5</itunes:episode>
              <itunes:explicit>yes</itunes:explicit>
              <itunes:image href="https://cdn.example.com/ep2.jpg"/>
              <podcast:chapters url="https://cdn.example.com/ep2.json" type="application/json+chapters"/>
              <podcast:transcript url="https://cdn.example.com/ep2.html" type="text/html"/>
              <podcast:transcript url="https://cdn.example.com/ep2.vtt" type="text/vtt"/>
              <podcast:transcript url="https://cdn.example.com/ep2.srt" type="application/x-subrip"/>
            </item>
            <item>
              <title>Trailer</title>
              <pubDate>Mon, 1 Jan 2024 08:00:00 EST</pubDate>
              <enclosure url="https://cdn.example.com/trailer.m4a" type="audio/x-m4a"/>
              <itunes:duration>95</itunes:duration>
              <itunes:episodeType>trailer</itunes:episodeType>
            </item>
            <item>
              <title>A video episode</title>
              <enclosure url="https://cdn.example.com/video.mp4" type="video/mp4"/>
            </item>
            <item>
              <title>A blog post</title>
            </item>
            <item>
              <title>The same guid again</title>
              <guid>ep-2</guid>
              <enclosure url="https://cdn.example.com/dupe.mp3" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    private static ParsedFeed Parse(string xml) => FeedParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void ReadsTheShow()
    {
        var feed = Parse(Feed);

        Assert.Equal("The History Hour", feed.Title);
        Assert.Equal("Jane Smith", feed.Author);
        Assert.Equal("A longer summary of the show.", feed.Description); // the longer of the two, as text
        Assert.Equal("https://cdn.example.com/show.jpg", feed.ImageUrl);
        Assert.Equal("en-gb", feed.Language);
        Assert.False(feed.Explicit);
        Assert.Equal(["History", "Society & Culture", "Documentary"], feed.Categories);
        Assert.Equal("https://feeds.example.com/new", feed.NewFeedUrl);
        Assert.Equal("9b024349-ccf0-5f69-a609-6b82873eab3c", feed.PodcastGuid);
    }

    [Fact]
    public void KeepsEpisodesWithSound_OnceEach()
    {
        var feed = Parse(Feed);

        Assert.Equal(["The Fall of Rome", "Trailer"], feed.Episodes.Select(e => e.Title));
        var episode = feed.Episodes[0];
        Assert.Equal("ep-2", episode.EpisodeGuid);
        Assert.Equal("https://cdn.example.com/ep2.mp3", episode.AudioUrl);
        Assert.Equal(12345678, episode.LengthBytes);
        Assert.Equal(new DateTime(2024, 9, 3, 9, 0, 0, DateTimeKind.Utc), episode.PublishedAt);
        Assert.Equal(3723, episode.DurationSeconds);
        Assert.Equal((2, 5), (episode.Season, episode.Episode));
        Assert.True(episode.Explicit);
        Assert.Equal("https://cdn.example.com/ep2.jpg", episode.ImageUrl);
        Assert.Equal("https://cdn.example.com/ep2.json", episode.ChaptersUrl);
        Assert.Equal(("https://cdn.example.com/ep2.srt", "application/x-subrip"), (episode.TranscriptUrl, episode.TranscriptType)); // SRT before WebVTT; HTML never
        Assert.Equal("Part one.\n\nWith links & more.\n\n• One\n• Two", episode.Description);

        var trailer = feed.Episodes[1];
        Assert.Equal("https://cdn.example.com/trailer.m4a", trailer.EpisodeGuid); // no guid: the file's address stands in
        Assert.Equal(PodcastEpisodeType.Trailer, trailer.EpisodeType);
        Assert.Equal(new DateTime(2024, 1, 1, 13, 0, 0, DateTimeKind.Utc), trailer.PublishedAt);
        Assert.Equal(95, trailer.DurationSeconds);
        Assert.False(trailer.Explicit); // the show's
    }

    [Theory]
    [InlineData("Tue, 03 Sep 2024 10:00:00 GMT", "2024-09-03T10:00:00")]
    [InlineData("Tue, 3 Sep 2024 10:00:00 -0700", "2024-09-03T17:00:00")]
    [InlineData("Mon, 03 Sep 2024 10:00:00 +0000", "2024-09-03T10:00:00")] // the wrong weekday
    [InlineData("03 Sep 2024 10:00 PDT", "2024-09-03T17:00:00")]
    [InlineData("2024-09-03T10:00:00Z", "2024-09-03T10:00:00")]
    [InlineData("Tuesday, 3 September 2024 10:00:00 +0000", "2024-09-03T10:00:00")]
    public void ReadsDatesAsFeedsWriteThem(string text, string expected) =>
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), FeedParser.Date(text));

    [Theory]
    [InlineData("3723", 3723.0)]
    [InlineData("62:03", 3723.0)]
    [InlineData("01:02:03", 3723.0)]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("about an hour", null)]
    [InlineData("0", null)]
    public void ReadsDurations(string text, double? expected) => Assert.Equal(expected, FeedParser.Duration(text));

    [Fact]
    public void RefusesWhatIsntAFeed()
    {
        Assert.Throws<FormatException>(() => Parse("<html><body>Not found</body></html>"));
        Assert.Throws<FormatException>(() => Parse("not xml at all"));
        Assert.Throws<FormatException>(() => Parse("<rss><channel><item/></channel></rss>")); // no title
    }

    [Fact]
    public void IgnoresDeclaredEntities()
    {
        // A billion laughs: refused or left unexpanded, never expanded.
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <rss><channel><title>&b;</title></channel></rss>
            """;
        try
        {
            Assert.DoesNotContain("aaaa", Parse(bomb).Title, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
        }
    }

    [Fact]
    public void OnlyWebAddressesCount()
    {
        Assert.Null(FeedParser.WebUrl("javascript:alert(1)"));
        Assert.Null(FeedParser.WebUrl("file:///etc/passwd"));
        Assert.Null(FeedParser.WebUrl("/relative.mp3"));
        Assert.Equal("http://example.com/a.mp3", FeedParser.WebUrl(" http://example.com/a.mp3 "));
    }
}
