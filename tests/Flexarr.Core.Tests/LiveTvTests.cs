using System.Text;
using Flexarr.Core.LiveTv;
using Flexarr.Streaming;
using Flexarr.Streaming.Live;

namespace Flexarr.Core.Tests;

public sealed class XmltvParserTests
{
    // As Dispatcharr serves it, with a DOCTYPE (never fetched) and the elements in the order guides use.
    private const string Guide = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv generator-info-name="Dispatcharr">
          <channel id="101"><display-name>BBC One</display-name><icon src="http://dispatcharr/logo/1"/></channel>
          <programme start="20261026190000 +0000" stop="20261026200000 +0000" channel="101">
            <title lang="en">Doctor Who</title>
            <sub-title lang="en">The Star Beast</sub-title>
            <desc lang="en">The Doctor meets Donna again.</desc>
            <category lang="en">Drama</category>
            <category lang="en">Sci-fi</category>
            <episode-num system="xmltv_ns">13.0/3.</episode-num>
            <episode-num system="onscreen">S14 E1</episode-num>
            <icon src="https://img.example/who.jpg"/>
            <new/>
          </programme>
          <programme start="20261026200000 +0100" stop="20261026203000 +0100" channel="101">
            <title>The News</title>
            <episode-num system="onscreen">S2 E7</episode-num>
          </programme>
          <programme start="20261026210000" stop="20261026200000" channel="101"><title>Ends before it starts</title></programme>
          <programme start="20261026220000 +0000" stop="20261026230000 +0000" channel="101"><desc>No title</desc></programme>
          <programme start="20261026220000 +0000" stop="20261026230000 +0000"><title>No channel</title></programme>
        </tv>
        """;

    private static async Task<List<XmltvProgramme>> ParseAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var list = new List<XmltvProgramme>();
        await foreach (var p in XmltvParser.ProgrammesAsync(stream))
        {
            list.Add(p);
        }

        return list;
    }

    [Fact]
    public async Task Reads_every_field_of_a_programme()
    {
        var who = (await ParseAsync(Guide))[0];

        Assert.Equal("101", who.ChannelId);
        Assert.Equal(new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc), who.Start);
        Assert.Equal(DateTimeKind.Utc, who.Start.Kind);
        Assert.Equal("Doctor Who", who.Title);
        Assert.Equal("The Star Beast", who.Subtitle); // the element straight after the title isn't skipped
        Assert.Equal("The Doctor meets Donna again.", who.Description);
        Assert.Equal("Drama", who.Category); // the first category
        Assert.Equal(14, who.Season); // xmltv_ns counts from zero
        Assert.Equal(1, who.Episode);
        Assert.Equal("https://img.example/who.jpg", who.ImageUrl);
        Assert.True(who.IsNew);
    }

    [Fact]
    public async Task Applies_the_offset_and_reads_on_screen_numbers()
    {
        var news = (await ParseAsync(Guide))[1];

        Assert.Equal(new DateTime(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc), news.Start);
        Assert.Equal((2, 7), (news.Season, news.Episode));
        Assert.False(news.IsNew);
        Assert.Null(news.Subtitle);
    }

    [Fact]
    public async Task Leaves_out_programmes_it_cannot_place()
    {
        Assert.Equal(["Doctor Who", "The News"], (await ParseAsync(Guide)).Select(p => p.Title));
    }

    [Theory]
    [InlineData("20261026063000 +0000", "2026-10-26T06:30:00")]
    [InlineData("20261026063000 +0400", "2026-10-26T02:30:00")]
    [InlineData("20261026063000 -0130", "2026-10-26T08:00:00")]
    [InlineData("20261026063000 +04:00", "2026-10-26T02:30:00")]
    [InlineData("202610260630", "2026-10-26T06:30:00")]
    public void Parses_times_as_utc(string text, string expected)
    {
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), XmltvParser.ParseTime(text));
    }

    [Theory]
    [InlineData("xmltv_ns", "0.4.", 1, 5)]
    [InlineData("xmltv_ns", ".4.0/1", null, 5)]
    [InlineData("xmltv_ns", "2/5..", 3, null)]
    [InlineData("onscreen", "S01E05", 1, 5)]
    [InlineData("onscreen", "Episode 5", null, null)]
    public void Reads_season_and_episode(string system, string value, int? season, int? episode)
    {
        Assert.Equal((season, episode), XmltvParser.ParseEpisode(system, value));
    }
}

public sealed class LivePlanTests
{
    [Fact]
    public void Copies_progressive_h264_with_aac_as_it_comes()
    {
        var plan = LivePlan.For(new LiveFormat("h264", 1280, 720, false, 25, "aac", 2));

        Assert.True(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
        Assert.Equal("Original", plan.Describe());
    }

    [Fact]
    public void Deinterlaces_broadcast_mpeg2_and_converts_its_sound()
    {
        var plan = LivePlan.For(new LiveFormat("mpeg2video", 720, 576, true, 25, "mp2", 2));

        Assert.False(plan.CopyVideo);
        Assert.True(plan.Deinterlace);
        Assert.False(plan.CopyAudio);
        Assert.Equal(576, plan.TargetHeight);
        Assert.Equal("Converted to 576p, deinterlaced", plan.Describe());
    }

    [Fact]
    public void Converts_interlaced_h264_and_caps_hevc_at_1080p()
    {
        Assert.False(LivePlan.For(new LiveFormat("h264", 1920, 1080, true, 50, "aac", 2)).CopyVideo);
        var hevc = LivePlan.For(new LiveFormat("hevc", 3840, 2160, false, 50, "eac3", 6));
        Assert.False(hevc.CopyVideo);
        Assert.Equal(1080, hevc.TargetHeight);
    }

    [Fact]
    public void Keeps_the_picture_when_only_the_sound_needs_converting()
    {
        var plan = LivePlan.For(new LiveFormat("h264", 1280, 720, false, 25, "ac3", 6));

        Assert.Equal("Original video, converted sound", plan.Describe());
    }

    [Fact]
    public void Away_from_home_everything_is_converted_to_at_most_720p()
    {
        var hd = LivePlan.ForAway(new LiveFormat("h264", 1920, 1080, false, 25, "aac", 2));
        Assert.False(hd.CopyVideo); // even a channel that copies at home
        Assert.False(hd.CopyAudio);
        Assert.Equal(720, hd.TargetHeight);
        Assert.Equal(2_500_000, hd.VideoBitrate);
        Assert.Equal("Reduced for away from home (720p)", hd.Describe());

        var sd = LivePlan.ForAway(new LiveFormat("mpeg2video", 720, 576, true, 25, "mp2", 2));
        Assert.Equal((576, 1_500_000L, true), (sd.TargetHeight, sd.VideoBitrate, sd.Deinterlace));
    }

    [Fact]
    public void Reads_the_first_video_and_audio_streams_skipping_cover_art()
    {
        const string json = """
            {"streams":[
              {"codec_type":"video","codec_name":"mjpeg","width":300,"height":300,"disposition":{"attached_pic":1}},
              {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"field_order":"tt","avg_frame_rate":"25/1"},
              {"codec_type":"audio","codec_name":"ac3","channels":6},
              {"codec_type":"audio","codec_name":"aac","channels":2}
            ]}
            """;

        Assert.Equal(new LiveFormat("h264", 1920, 1080, true, 25, "ac3", 6), LiveProbe.Parse(json));
    }

    [Fact]
    public void A_stream_with_nothing_playable_is_refused()
    {
        Assert.Throws<LiveStreamException>(() => LiveProbe.Parse("""{"streams":[{"codec_type":"data"}]}"""));
    }
}

public sealed class LiveCommandTests
{
    private static readonly TranscodeSettings Settings = new() { CacheDirectory = "/cache", HardwareAcceleration = HardwareAcceleration.VideoToolbox };
    private static readonly LiveSource Source = new("http://dispatcharr:9191/live/flexarr/s3cret/7.ts", "s3cret");

    [Fact]
    public void Copies_into_a_rolling_playlist_that_keeps_the_rewind_window()
    {
        var args = LiveCommand.Hls(Source, new LivePlan(true, false, 720, 4_000_000, true), "/out", Settings, TimeSpan.FromHours(1), resume: false);

        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.Equal("900", After(args, "-hls_list_size")); // an hour of 4-second segments
        Assert.Contains("delete_segments", After(args, "-hls_flags"), StringComparison.Ordinal);
        Assert.DoesNotContain("append_list", After(args, "-hls_flags"), StringComparison.Ordinal);
        Assert.Equal("http,https,tcp,tls", After(args, "-protocol_whitelist"));
        Assert.DoesNotContain("-hwaccel", args); // nothing decoded, nothing to accelerate
        Assert.Equal(Path.Combine("/out", "live.m3u8"), args[^1]);
    }

    [Fact]
    public void Converts_on_the_media_engine_deinterlacing_a_frame_per_field()
    {
        var args = LiveCommand.Hls(Source, new LivePlan(false, true, 576, 2_500_000, false), "/out", Settings, TimeSpan.FromMinutes(30), resume: false);

        Assert.Equal("h264_videotoolbox", After(args, "-c:v"));
        Assert.StartsWith("bwdif=mode=send_field", After(args, "-vf"), StringComparison.Ordinal);
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("expr:gte(t,n_forced*4)", After(args, "-force_key_frames"));
    }

    [Fact]
    public void A_restart_carries_on_the_same_playlist()
    {
        var args = LiveCommand.Hls(Source, new LivePlan(true, false, 720, 4_000_000, true), "/out", Settings, TimeSpan.FromHours(1), resume: true);

        Assert.Contains("append_list+discont_start", After(args, "-hls_flags"), StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_lose_the_password_and_the_address()
    {
        var raw = Source.Redact("http://dispatcharr:9191/live/flexarr/s3cret/7.ts: Server returned 5XX Server Error reply");
        var error = Assert.IsType<LiveStreamException>(LiveStreamException.FromError(raw));

        Assert.DoesNotContain("s3cret", raw, StringComparison.Ordinal);
        Assert.True(error.Busy);
        Assert.DoesNotContain("dispatcharr", error.Message, StringComparison.Ordinal);
        Assert.Contains("No connection free", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server returned 403 Forbidden", "refused Flexarr's sign-in", false)]
    [InlineData("Server returned 404 Not Found", "doesn't have this channel", false)]
    [InlineData("http://x/live/a/b/1.ts: Input/output error", "isn't sending this channel", false)]
    [InlineData("Server returned 503 Service Unavailable", "No connection free", true)]
    public void Says_why_a_channel_would_not_play(string ffmpeg, string expected, bool busy)
    {
        var error = LiveStreamException.FromError(ffmpeg);

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal(busy, error.Busy);
    }

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];
}

public sealed class LiveScheduleTests
{
    private static readonly Guid Channel = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    private static LiveProgramme At(string title, double start, double end, Guid? channel = null) => new()
    {
        ChannelId = channel ?? Channel,
        Title = title,
        Start = Day.AddHours(start),
        End = Day.AddHours(end),
    };

    [Fact]
    public void Keeps_the_later_of_two_schedules()
    {
        // The guide's ITV2 on 26 October 2026: its schedule twice, one copy an hour early. The later one is right
        // (Open-EPG agrees: Potomac at 07:30).
        var tidy = LiveSchedule.Tidy([
            At("Dress to Impress", 5 + 25 / 60.0, 6.5),
            At("Backyard Builds", 6, 6 + 25 / 60.0),
            At("Dress to Impress", 6 + 25 / 60.0, 7.5),
            At("Potomac", 6.5, 7 + 20 / 60.0),
            At("Beverly Hills", 7 + 20 / 60.0, 8.25),
            At("Potomac", 7.5, 8 + 20 / 60.0),
            At("Beverly Hills", 8 + 20 / 60.0, 9.25),
        ]);

        Assert.Equal(["Backyard Builds", "Dress to Impress", "Potomac", "Beverly Hills"], tidy.Select(p => p.Title));
        Assert.Equal(Day.AddHours(6), tidy[0].Start);
        Assert.Equal(Day.AddHours(7.5), tidy[2].Start);
        Assert.All(tidy.Zip(tidy.Skip(1)), pair => Assert.True(pair.Second.Start >= pair.First.End));
    }

    [Fact]
    public void Keeps_hourly_bulletins()
    {
        // A news channel: the same title every hour, nothing clashing.
        var tidy = LiveSchedule.Tidy([At("BBC News", 6, 7), At("BBC News", 7, 8), At("BBC News", 8, 9)]);

        Assert.Equal(3, tidy.Count);
    }

    [Fact]
    public void Trims_a_small_overlap()
    {
        var tidy = LiveSchedule.Tidy([At("News", 18, 18.5 + 2 / 60.0), At("Weather", 18.5, 19)]);

        Assert.Equal(2, tidy.Count);
        Assert.Equal(tidy[0].End, tidy[1].Start);
    }

    [Fact]
    public void Keeps_channels_apart()
    {
        var other = Guid.NewGuid();

        Assert.Equal(2, LiveSchedule.Tidy([At("One", 18, 19), At("Two", 18, 19, other)]).Count);
    }
}

public sealed class CommunityLogosTests
{
    // A slice of the collection's real index (github.com/tv-logo/tv-logos), around the channels below.
    private static readonly CommunityLogos Logos = new(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tv-logos-paths.txt")));

    [Theory]
    [InlineData("BBC 1 HD", "UK| GENERAL ᴴᴰ/ᴿᴬᵂ", "united-kingdom/bbc-one-uk.png")]
    [InlineData("BBC 3 / CBBC HD", "UK| GENERAL", "united-kingdom/bbc-three-cbbc-uk.png")]
    [InlineData("BBC ONE LONDON 4K", "UK| GENERAL", "united-kingdom/bbc-one-uk.png")]
    [InlineData("ITV 1 HD", "UK| GENERAL", "united-kingdom/itv-1-uk.png")]
    [InlineData("ITV 2+1", "UK| GENERAL", "united-kingdom/itv-2-plus-uk.png")]
    [InlineData("CHANNEL 4", "UK| GENERAL", "united-kingdom/channel-4-uk.png")]
    [InlineData("E4", "UK| GENERAL", "united-kingdom/e-4-uk.png")]
    [InlineData("MORE4", "UK| GENERAL", "united-kingdom/4-more-uk.png")]
    [InlineData("FILM 4 4K", "UK| GENERAL", "united-kingdom/film-4-uk.png")]
    [InlineData("UK: DAVE ◉", null, "united-kingdom/dave-uk.png")]
    [InlineData("BBC NEWS HEVC", "UK| NEWS", "united-kingdom/bbc-news-uk.png")]
    [InlineData("AL JAZEERA EN HD", "UK| NEWS", "united-kingdom/aljazeera-uk.png")]
    [InlineData("FRANCE 24", "UK| NEWS", "international/france-24-int.png")]
    [InlineData("DW News", "UK| NEWS", "international/dw-int.png")]
    [InlineData("ITVBe", "UK", "united-kingdom/itv-be-uk.png")]
    [InlineData("5USA", "UK", "united-kingdom/5-usa-uk.png")]
    [InlineData("Sky Sports F1", "UK| SPORT", "united-kingdom/sky-sports-f1-uk.png")]
    [InlineData("Sky Sports+", "UK| SPORT", "united-kingdom/sky-sports-plus-hz-uk.png")]
    [InlineData("MSNBC", "US| NEWS", "united-states/msnbc-hz-us.png")]
    public void Finds_the_channels_current_logo(string name, string? group, string expected)
    {
        Assert.Equal(CommunityLogos.Root + "countries/" + expected, Logos.Find(name, group));
    }

    [Fact]
    public void A_channel_that_names_no_country_is_looked_for_where_the_line_up_is()
    {
        // Groups like "Entertainment" say nothing; the rest of the line-up does ("UK"), so Comedy Central is the UK one.
        var home = CommunityLogos.HomeCountry([("BBC One", "UK"), ("ITV1", "UK"), ("Comedy Central", "Entertainment"), ("CNN", "US| NEWS")]);
        Assert.Equal("uk", home);
        Assert.Equal(CommunityLogos.Root + "countries/united-kingdom/comedy-central-uk.png", Logos.Find("Comedy Central", "Entertainment", home));
        Assert.Equal(CommunityLogos.Root + "countries/united-kingdom/comedy-central-uk.png", Logos.Find("Comedy Central", null, "gb"));
    }

    [Fact]
    public void Leaves_a_channel_it_cannot_place_to_the_source()
    {
        Assert.Null(Logos.Find("Totally Unknown Channel", "UK| GENERAL"));
        Assert.Null(Logos.Find("HD", null));
    }

    [Fact]
    public void Only_the_collection_counts_as_community()
    {
        Assert.True(CommunityLogos.IsCommunity(new Uri(CommunityLogos.Root + "countries/united-kingdom/e-4-uk.png")));
        Assert.False(CommunityLogos.IsCommunity(new Uri("https://raw.githubusercontent.com/someone/else/main/e4.png")));
        Assert.False(CommunityLogos.IsCommunity(new Uri("http://raw.githubusercontent.com/tv-logo/tv-logos/main/x.png")));
    }
}
