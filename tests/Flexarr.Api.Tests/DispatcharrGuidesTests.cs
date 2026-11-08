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
        public int UserLevel { get; init; } = 10;
        /// <summary>What the source does after it's asked: its download's state, one per look.</summary>
        public Queue<(string Status, string UpdatedAt)> Next { get; } = new();

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
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (path == "/api/accounts/users/me/")
            {
                return Json($$"""{"user_level":{{UserLevel}}}""");
            }

            if (Asked.Count > 0 && Next.TryDequeue(out var next))
            {
                (Status, UpdatedAt) = next;
            }

            return Json($$"""
                [{"id":1,"source_type":"xmltv","is_active":true,"status":"{{Status}}","updated_at":"{{UpdatedAt}}","url":"http://provider/secret","password":"s3cret"},
                 {"id":2,"source_type":"xmltv","is_active":false,"status":"disabled","updated_at":"x"},
                 {"id":3,"source_type":"dummy","is_active":true,"status":"idle","updated_at":"x"}]
                """);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static DispatcharrGuides Guides(FakeAdminApi api, TimeSpan? waitAtMost = null) =>
        new(new HttpClient(api)) { CheckEvery = TimeSpan.FromMilliseconds(1), WaitAtMost = waitAtMost ?? TimeSpan.FromSeconds(20) };

    [Fact]
    public async Task Downloads_each_active_source_and_waits_for_it_to_finish()
    {
        using var api = new FakeAdminApi();
        // Not yet started, then downloading, then done.
        api.Next.Enqueue(("success", "2026-11-08T06:00:00Z"));
        api.Next.Enqueue(("fetching", "2026-11-08T11:00:00Z"));
        api.Next.Enqueue(("success", "2026-11-08T11:01:00Z"));

        Assert.Equal(GuideDownloadProblem.None, await Guides(api).DownloadAsync(Root, Key, CancellationToken.None));
        Assert.Equal(["/api/epg/import/ 1"], api.Asked);
        Assert.Equal(Key, api.KeySeen);
        Assert.Empty(api.Next);
    }

    [Fact]
    public async Task Says_when_a_source_fails_and_gives_up_waiting_in_the_end()
    {
        using var failing = new FakeAdminApi();
        failing.Next.Enqueue(("error", "2026-11-08T11:01:00Z"));
        Assert.Equal(GuideDownloadProblem.SourceFailed, await Guides(failing).DownloadAsync(Root, Key, CancellationToken.None));

        using var stuck = new FakeAdminApi();
        Assert.Equal(GuideDownloadProblem.TimedOut, await Guides(stuck, TimeSpan.FromMilliseconds(50)).DownloadAsync(Root, Key, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GuideDownloadProblem.KeyRefused)]
    [InlineData(HttpStatusCode.Forbidden, GuideDownloadProblem.KeyRefused)]
    [InlineData(HttpStatusCode.NotFound, GuideDownloadProblem.NotSupported)]
    [InlineData(HttpStatusCode.Found, GuideDownloadProblem.NotSupported)]
    [InlineData(HttpStatusCode.BadGateway, GuideDownloadProblem.Unreachable)]
    public async Task A_refusal_or_an_older_dispatcharr_is_put_in_words(HttpStatusCode answer, GuideDownloadProblem expected)
    {
        using var api = new FakeAdminApi { Answer = answer };
        Assert.Equal(expected, await Guides(api).DownloadAsync(Root, Key, CancellationToken.None));
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
}
