using System.Net;
using System.Text;
using System.Text.Json;
using Flexarr.Api.LiveTv;

namespace Flexarr.Api.Tests;

public sealed class DispatcharrGuidesTests
{
    private const string Key = "admin-k3y";
    private static readonly Uri Root = new("http://dispatcharr:9191/");

    /// <summary>Stands in for Dispatcharr's admin API: a real source, a switched-off one and a dummy.</summary>
    private sealed class FakeAdminApi : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public string? KeySeen { get; private set; }
        public HttpStatusCode? Answer { get; init; }
        public string Status { get; set; } = "success";
        public string UpdatedAt { get; set; } = "2026-11-08T06:00:00Z";
        public string Message { get; set; } = "";
        public string SourceType { get; init; } = "xmltv";
        public int UserLevel { get; init; } = 10;
        /// <summary>The import of this source is refused.</summary>
        public int? RefuseImport { get; init; }
        /// <summary>This many looks at the sources fail once one has been asked.</summary>
        public int BadLooks { get; set; }
        /// <summary>What the source does after it's asked: its download's state, one per look.</summary>
        public Queue<(string Status, string UpdatedAt)> Next { get; } = new();

        /// <summary>What it says of the key, in place of its level.</summary>
        public string? Me { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            KeySeen = request.Headers.GetValues("X-API-Key").Single();
            var path = request.RequestUri!.AbsolutePath;
            if (Answer is { } answer)
            {
                return new HttpResponseMessage(answer);
            }

            if (request.Method == HttpMethod.Post)
            {
                var id = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("id").GetInt32();
                Asked.Add($"{path} {id}");
                return new HttpResponseMessage(id == RefuseImport ? HttpStatusCode.BadGateway : HttpStatusCode.Accepted);
            }

            if (path == "/api/accounts/users/me/")
            {
                return Json(Me ?? $$"""{"user_level":{{UserLevel}}}""");
            }

            if (Asked.Count > 0 && BadLooks > 0)
            {
                BadLooks--;
                throw new HttpRequestException("Not answering");
            }

            if (Asked.Count > 0 && Next.TryDequeue(out var next))
            {
                (Status, UpdatedAt) = next;
            }

            return Json($$"""
                [{"id":1,"source_type":"{{SourceType}}","is_active":true,"status":"{{Status}}","updated_at":"{{UpdatedAt}}","last_message":"{{Message}}","url":"http://provider/secret","password":"s3cret"},
                 {{SecondSource}}
                 {"id":2,"source_type":"xmltv","is_active":false,"status":"disabled","updated_at":"x"},
                 {"id":3,"source_type":"dummy","is_active":true,"status":"idle","updated_at":"x"}]
                """);
        }

        public string SecondSource { get; init; } = "";

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static DispatcharrGuides Guides(FakeAdminApi api, TimeSpan? waitAtMost = null, TimeSpan? startsWithin = null) =>
        new(new HttpClient(api)) { CheckEvery = TimeSpan.FromMilliseconds(1), WaitAtMost = waitAtMost ?? TimeSpan.FromSeconds(20), StartsWithin = startsWithin ?? TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Downloads_each_active_source_and_waits_for_it_to_finish()
    {
        using var api = new FakeAdminApi();
        // Not yet started, then downloading, then done.
        api.Next.Enqueue(("success", "2026-11-08T06:00:00Z"));
        api.Next.Enqueue(("fetching", "2026-11-08T06:00:00Z"));
        api.Next.Enqueue(("parsing", "2026-11-08T06:00:00Z"));
        api.Next.Enqueue(("success", "2026-11-08T11:01:00Z"));

        Assert.Equal(new GuideDownload(GuideDownloadProblem.None, true), await Guides(api).DownloadAsync(Root, Key, CancellationToken.None));
        Assert.Equal(["/api/epg/import/ 1"], api.Asked);
        Assert.Equal(Key, api.KeySeen);
        Assert.Empty(api.Next);
    }

    [Fact]
    public async Task A_failed_download_is_seen_straight_away_though_dispatcharr_leaves_its_stamp_alone()
    {
        using var failing = new FakeAdminApi();
        failing.Next.Enqueue(("fetching", failing.UpdatedAt));
        failing.Next.Enqueue(("error", failing.UpdatedAt));
        Assert.Equal(GuideDownloadProblem.SourceFailed, (await Guides(failing).DownloadAsync(Root, Key, CancellationToken.None)).Problem);

        // Too quick to be seen downloading, from a source already in error: only the message differs.
        using var again = new FakeAdminApi { Status = "error", Message = "Timed out" };
        var guides = Guides(again, startsWithin: TimeSpan.FromMilliseconds(30));
        Assert.Equal(GuideDownloadProblem.SourceFailed, (await guides.DownloadAsync(Root, Key, CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task A_source_that_never_starts_isnt_waited_for_but_a_slow_one_is_given_up_on_in_the_end()
    {
        using var idle = new FakeAdminApi();
        Assert.Equal(GuideDownloadProblem.None, (await Guides(idle, startsWithin: TimeSpan.FromMilliseconds(30)).DownloadAsync(Root, Key, CancellationToken.None)).Problem);

        using var stuck = new FakeAdminApi();
        Assert.Equal(GuideDownloadProblem.TimedOut, (await Guides(stuck, TimeSpan.FromMilliseconds(50)).DownloadAsync(Root, Key, CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task A_bad_look_or_two_doesnt_end_the_wait_but_dispatcharr_going_away_does()
    {
        using var blip = new FakeAdminApi { BadLooks = 3 };
        blip.Next.Enqueue(("success", "2026-11-08T11:01:00Z"));
        Assert.Equal(new GuideDownload(GuideDownloadProblem.None, true), await Guides(blip).DownloadAsync(Root, Key, CancellationToken.None));

        using var gone = new FakeAdminApi { BadLooks = 100 };
        Assert.Equal(new GuideDownload(GuideDownloadProblem.Unreachable, true), await Guides(gone).DownloadAsync(Root, Key, CancellationToken.None));
    }

    [Fact]
    public async Task Failing_part_way_through_says_that_a_source_was_asked()
    {
        using var api = new FakeAdminApi
        {
            RefuseImport = 4,
            SecondSource = """{"id":4,"source_type":"xmltv","is_active":true,"status":"idle","updated_at":"x"},""",
        };
        Assert.Equal(new GuideDownload(GuideDownloadProblem.Unreachable, true), await Guides(api).DownloadAsync(Root, Key, CancellationToken.None));
        Assert.Equal(2, api.Asked.Count);
    }

    [Fact]
    public async Task Schedules_direct_isnt_asked_within_the_wait_dispatcharr_holds_it_to()
    {
        using var recent = new FakeAdminApi { SourceType = "schedules_direct", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal(new GuideDownload(GuideDownloadProblem.None, false), await Guides(recent).DownloadAsync(Root, Key, CancellationToken.None));
        Assert.Empty(recent.Asked);

        using var due = new FakeAdminApi { SourceType = "schedules_direct", UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        due.Next.Enqueue(("success", "now"));
        await Guides(due).DownloadAsync(Root, Key, CancellationToken.None);
        Assert.Single(due.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GuideDownloadProblem.KeyRefused)]
    [InlineData(HttpStatusCode.Forbidden, GuideDownloadProblem.KeyRefused)]
    [InlineData(HttpStatusCode.NotFound, GuideDownloadProblem.NotSupported)]
    [InlineData(HttpStatusCode.Found, GuideDownloadProblem.Unreachable)]
    [InlineData(HttpStatusCode.BadGateway, GuideDownloadProblem.Unreachable)]
    public async Task A_refusal_or_an_older_dispatcharr_is_put_in_words(HttpStatusCode answer, GuideDownloadProblem expected)
    {
        using var api = new FakeAdminApi { Answer = answer };
        Assert.Equal(new GuideDownload(expected, false), await Guides(api).DownloadAsync(Root, Key, CancellationToken.None));
        Assert.Empty(api.Asked);
    }

    [Theory]
    [InlineData(10, GuideDownloadProblem.None)]
    [InlineData(1, GuideDownloadProblem.KeyRefused)]
    public async Task Checking_a_key_wants_an_admins(int level, GuideDownloadProblem expected)
    {
        using var api = new FakeAdminApi { UserLevel = level };
        Assert.Equal(expected, await Guides(api).CheckKeyAsync(Root, Key, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"role":"admin"}""")]
    [InlineData("""{"user_level":"admin"}""")]
    [InlineData("[]")]
    public async Task An_answer_that_doesnt_give_a_level_doesnt_refuse_the_key(string me)
    {
        using var api = new FakeAdminApi { Me = me };
        Assert.Equal(GuideDownloadProblem.None, await Guides(api).CheckKeyAsync(Root, Key, CancellationToken.None));
    }
}
