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

    [Fact]
    public void ADateThatsPagesOfCommas_IsNoDate_AndDoesntRunOutOfStack()
    {
        // Each comma near the start was taken for a weekday to drop, one call deeper: 60,000 characters of this stopped the server.
        Assert.Null(FeedParser.Date(string.Concat(Enumerable.Repeat("a,", 200_000))));
        Assert.Null(FeedParser.Date("Mon, " + new string('x', 200)));
        // Padded with spaces is still a date.
        Assert.Equal(new DateTime(2024, 9, 3, 10, 0, 0, DateTimeKind.Utc), FeedParser.Date("Tue,   03 Sep 2024" + new string(' ', 200) + "10:00:00 GMT"));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("title")]
    [InlineData("pubDate")]
    public void AFeedOfTagsInsideTagsWithoutEnd_IsRefused_AndDoesntRunOutOfStack(string tag)
    {
        // Reading a tag's text went a call deeper for every tag inside it: 30,000 of them stopped the server. Before
        // that, building the document checked every tag around each one added: these 200,000 took minutes of a core.
        var deep = string.Concat(Enumerable.Repeat("<a>", 200_000)) + "hi" + string.Concat(Enumerable.Repeat("</a>", 200_000));
        var item = $"<item><title>One</title><guid>1</guid><{tag}>{deep}</{tag}><enclosure url=\"https://cdn.example.com/1.mp3\" type=\"audio/mpeg\"/></item>";

        Assert.Throws<FormatException>(() => Parse($"<rss><channel><title>Deep</title>{item}</channel></rss>"));
        Assert.Throws<FormatException>(() => Parse($"<rss><channel><title>Deep</title><{tag}>{deep}</{tag}>{item}</channel></rss>"));
    }

    [Fact]
    public void ShowNotesWrittenAsTagsInTheFeed_AreStillRead_HoweverManyOfThemSitSideBySide()
    {
        // As deep as is read at all, beside a thousand shallow ones: it's depth that's counted, not how many there are.
        var inside = FeedParser.MaxDepth - 3; // the last has rss, channel, item, description and the rest around it
        var nested = string.Concat(Enumerable.Repeat("<div>", inside)) + "Deep down" + string.Concat(Enumerable.Repeat("</div>", inside));
        var wide = string.Concat(Enumerable.Repeat("<p><b>Wide</b></p>", 1000));
        var feed = Parse($"<rss><channel><title>Notes</title><item><title>One</title><guid>1</guid><description>{wide}{nested}<p><b>After</b></p></description><enclosure url=\"https://cdn.example.com/1.mp3\" type=\"audio/mpeg\"/></item></channel></rss>");

        Assert.EndsWith("WideDeep downAfter", Assert.Single(feed.Episodes).Description, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => Parse($"<rss><channel><title>Notes</title><item><title>One</title><guid>1</guid><description><div>{nested}</div></description><enclosure url=\"https://cdn.example.com/1.mp3\" type=\"audio/mpeg\"/></item></channel></rss>"));
    }

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

    [Theory]
    [InlineData("<")] // every one the start of a tag that never ends
    [InlineData("<li")]
    [InlineData("< ")]
    [InlineData("<br ")]
    public async Task ADescriptionOfTagsThatNeverEnd_IsReadInStepWithItsLength(string piece)
    {
        // 5 MB of it took about 18 minutes when the tags were matched by backtracking.
        var html = string.Concat(Enumerable.Repeat(piece, 5_000_000 / piece.Length));

        var reading = Task.Run(() => FeedParser.PlainText(html));

        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.True((await reading)?.Length <= 20_000);
    }

    [Fact]
    public async Task AFeedWhoseEveryDescriptionIsTagsThatNeverEnd_IsReadInStepWithItsLength()
    {
        // Each description is as long as is read at all, so it's the matching that has to keep up: 40 of these took over
        // a minute by backtracking.
        var notes = new string('<', FeedParser.MaxHtmlLength);
        var items = string.Concat(Enumerable.Range(1, 40).Select(i =>
            $"<item><title>Episode {i}</title><guid>ep-{i}</guid><description><![CDATA[{notes}]]></description><enclosure url=\"https://cdn.example.com/ep{i}.mp3\" type=\"audio/mpeg\"/></item>"));

        var reading = Task.Run(() => Parse($"<rss><channel><title>Slow</title>{items}</channel></rss>"));

        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal(40, (await reading).Episodes.Count);
    }

    [Fact]
    public void ADescriptionLongerThanIsRead_IsCutBeforeATagLeftHalfOpen()
    {
        var link = "<a href=\"https://example.com/somewhere\">a link</a>";
        var html = "<p>Kept</p>" + new string(' ', FeedParser.MaxHtmlLength - "<p>Kept</p>".Length - 10) + link;

        Assert.Equal("Kept", FeedParser.PlainText(html));
        // Within the length, it's all read as before.
        Assert.Equal("Kept\n\na link", FeedParser.PlainText("<p>Kept</p>" + link));
    }

    [Fact]
    public void AFeedOfMoreEpisodesThanAnyShowHas_KeepsTheNewest_InItsOwnOrder()
    {
        // Oldest first, a day apart, five more than are read.
        var first = new DateTime(1960, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var items = new StringBuilder();
        for (var i = 1; i <= FeedParser.MaxEpisodes + 5; i++)
        {
            items.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"<item><title>Episode {i}</title><guid>ep-{i}</guid><pubDate>{first.AddDays(i):R}</pubDate><enclosure url=\"https://cdn.example.com/ep{i}.mp3\" type=\"audio/mpeg\"/></item>");
        }

        var feed = Parse($"<rss><channel><title>Endless</title>{items}</channel></rss>");

        Assert.Equal(FeedParser.MaxEpisodes, feed.Episodes.Count);
        Assert.Equal("ep-6", feed.Episodes[0].EpisodeGuid);
        Assert.Equal($"ep-{FeedParser.MaxEpisodes + 5}", feed.Episodes[^1].EpisodeGuid);
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
