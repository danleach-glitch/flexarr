using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flexarr.Api.Logging;

namespace Flexarr.Api.Tests;

/// <summary>The server's log: keeping secrets out, reading it back, and what apps and browsers report to it.</summary>
public class LoggingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Theory]
    [InlineData("GET /x?api_key=abc123&page=2", "GET /x?api_key=***&page=2")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload", "Authorization: Bearer ***")]
    [InlineData("{\"password\":\"hunter2\"}", "{\"password\":\"***\"}")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void RedactsNamedSecrets(string line, string expected) => Assert.Equal(expected, Redactor.Redact(line));

    [Fact]
    public void HexTokensBecomeTheirPlaybackId()
    {
        var sid = "74b4b04452029150251b60f311e97669";

        var line = Redactor.Redact($"Transcode {sid}: starting at /api/v1/playback/{sid}/main.m3u8");

        Assert.DoesNotContain(sid, line, StringComparison.Ordinal);
        Assert.Equal($"Transcode #{Redactor.ShortHash(sid)}: starting at /api/v1/playback/#{Redactor.ShortHash(sid)}/main.m3u8", line);
    }

    [Fact]
    public void RedactsConfiguredSecretsAnywhere()
    {
        Redactor.UseSecrets(["sonarr-key-xyz"]);
        try
        {
            Assert.Equal("calling with *** in the header", Redactor.Redact("calling with sonarr-key-xyz in the header"));
        }
        finally
        {
            Redactor.UseSecrets([]);
        }
    }

    [Theory]
    [InlineData("Flexarr.Streaming.TranscodeSession", "playback")]
    [InlineData("Flexarr.Streaming.Live.LiveSession", "livetv")]
    [InlineData("Flexarr.Api.Scanning.LibraryScanWorker", "library")]
    [InlineData("System.Net.Http.HttpClient.ArrClient.LogicalHandler", "integrations")]
    [InlineData("Flexarr.StreamingExtras", "general")]
    [InlineData(null, "general")]
    public void SortsLoggersIntoAreas(string? source, string area) => Assert.Equal(area, LogAreas.Of(source));

    [Fact]
    public void ReadsLinesBackwardsAcrossChunks()
    {
        var lines = Enumerable.Range(0, 5000).Select(i => $"line {i} " + new string('x', i % 50)).ToList();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));

        var read = LogReader.LinesBackwards(stream, stream.Length).ToList();

        Assert.Equal(Enumerable.Reverse(lines), read.Select(r => r.Line));
        Assert.Equal(0, read[^1].Start);
    }

    [Fact]
    public void PagesBackThroughTheFilesWithACursor()
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-logs-").FullName;
        File.WriteAllLines(Path.Combine(folder, "flexarr-20261025.log"), Enumerable.Range(0, 3).Select(i => Line($"old {i}", "Warning")));
        File.WriteAllLines(Path.Combine(folder, "flexarr-20261026.log"), Enumerable.Range(0, 3).Select(i => Line($"new {i}", i == 1 ? "Error" : null)));
        var reader = new LogReader(new LogPaths(folder));

        var first = reader.Read(new LogQuery(null, null, null, null, 4));
        var second = reader.Read(new LogQuery(null, null, null, first.NextCursor, 4));
        var errors = reader.Read(new LogQuery(Serilog.Events.LogEventLevel.Warning, null, null, null, 10));

        Assert.Equal(["new 2", "new 1", "new 0", "old 2"], first.Entries.Select(e => e.Message));
        Assert.Equal(["old 1", "old 0"], second.Entries.Select(e => e.Message));
        Assert.Null(second.NextCursor);
        Assert.Equal(["new 1", "old 2", "old 1", "old 0"], errors.Entries.Select(e => e.Message));

        static string Line(string message, string? level) =>
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["@t"] = "2026-10-26T10:00:00Z", ["@m"] = message, ["@l"] = level, ["SourceContext"] = "Flexarr.Test" });
    }

    [Fact]
    public async Task AppsReportErrorsIntoTheLog()
    {
        var device = await DeviceAsync("Logging test TV");
        var marker = Guid.NewGuid().ToString("N")[..12];

        var sent = await device.PostAsJsonAsync(Api("/logs/client"), new
        {
            platform = "tvos",
            appVersion = "1.0",
            entries = new[] { new { level = "Error", category = "playback", message = $"Player failed {marker} {{not a template}}", properties = new { playbackId = "abcd1234" } } },
        });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);

        var entry = await WaitForAsync(marker);
        Assert.Equal($"Player failed {marker} {{not a template}}", entry.GetProperty("message").GetString());
        Assert.Equal("clients", entry.GetProperty("area").GetString());
        Assert.Equal("Logging test TV", entry.GetProperty("properties").GetProperty("device").GetString());
        Assert.Equal("abcd1234", entry.GetProperty("properties").GetProperty("playbackId").GetString());
    }

    [Fact]
    public async Task AReportCantForgeLogLines()
    {
        var device = await DeviceAsync("Forging test TV");
        var marker = Guid.NewGuid().ToString("N")[..12];

        await device.PostAsJsonAsync(Api("/logs/client"), new
        {
            platform = "web",
            entries = new[]
            {
                new
                {
                    level = "Error",
                    message = $"ok {marker}\r\n2026-10-27 09:00:00.000 +00:00 [ERR] Flexarr.Api: fake\u001b[31m",
                    error = "TypeError: x\n    at play (app.js:1)",
                    properties = new { playbackId = "a\nb" },
                },
            },
        });

        var entry = await WaitForAsync(marker);
        Assert.Equal($"ok {marker}⏎2026-10-27 09:00:00.000 +00:00 [ERR] Flexarr.Api: fake[31m", entry.GetProperty("message").GetString());
        Assert.Equal("a⏎b", entry.GetProperty("properties").GetProperty("playbackId").GetString());
        Assert.Equal("TypeError: x\n    at play (app.js:1)", entry.GetProperty("exception").GetString());
    }

    [Fact]
    public async Task OnlyAdminsReadTheLog()
    {
        var anonymous = await factory.CreateClient().GetAsync(Api("/logs"));
        var reporting = await factory.CreateClient().PostAsJsonAsync(Api("/logs/client"),
            new { platform = "web", entries = new[] { new { level = "Error", message = "x" } } });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reporting.StatusCode);
    }

    [Fact]
    public async Task ADeviceThatSendsTooMuchIsTurnedAway()
    {
        var device = await DeviceAsync("Chatty TV");
        var batch = new { platform = "tvos", entries = Enumerable.Range(0, 100).Select(i => new { level = "Warning", message = $"loop {i}" }).ToArray() };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await device.PostAsJsonAsync(Api("/logs/client"), batch)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task DiagnosticsAreRedactedAndListed()
    {
        var device = await DeviceAsync("Diagnostics TV");
        using var body = new StringContent($"started\nX-Api-Key: {FlexarrApiFactory.ApiKey}\npassword=hunter2\n", Encoding.UTF8, "text/plain");

        var sent = await device.PostAsync(Api("/logs/client/diagnostics?platform=tvos&appVersion=1.0&note=stutters"), body);
        var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var uploads = await Admin().GetFromJsonAsync<JsonElement>(Api("/logs/uploads"));
        var text = await Admin().GetStringAsync(Api($"/logs/uploads/{id}"));

        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Contains(uploads.EnumerateArray(), u => u.GetProperty("id").GetString() == id && u.GetProperty("device").GetString() == "Diagnostics TV");
        Assert.Contains("started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync(Api("/logs/uploads/..%2Fconfig"))).StatusCode);
    }

    [Fact]
    public async Task AreaLevelsChangeOneAtATime()
    {
        var admin = Admin();
        admin.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");

        await admin.PutAsJsonAsync(Api("/logs/settings"), new { areas = new[] { new { key = "playback", level = "Debug" } } });
        var both = await (await admin.PutAsJsonAsync(Api("/logs/settings"), new { areas = new[] { new { key = "library", level = "Warning" } } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var reset = await (await admin.PutAsJsonAsync(Api("/logs/settings"), new { areas = new object[] { new { key = "playback", level = (string?)null }, new { key = "library", level = (string?)null } } }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Debug", Level(both, "playback"));
        Assert.Equal("Warning", Level(both, "library"));
        Assert.Null(Level(reset, "playback"));

        static string? Level(JsonElement settings, string key) =>
            settings.GetProperty("areas").EnumerateArray().First(a => a.GetProperty("key").GetString() == key).GetProperty("level").GetString();
    }

    [Theory]
    [InlineData("Couldn't open '/Volumes/Media/data/movies/Dune (2021)/Dune.mkv'", "Couldn't open \"…\"")]
    [InlineData("Probe failed for /Volumes/Media/data/movies/Dune (2021)/Dune Part Two.mkv: timeout", "Probe failed for <path> Part <file>: timeout")]
    [InlineData("Could not find a part of the path /Users/dan/.flexarr/data/cache/x", "Could not find a part of the path <path>")]
    [InlineData("Sonarr at http://192.168.1.106:8989/api/v3/series failed", "Sonarr at <server>/api/v3/series failed")]
    [InlineData("connect to 10.0.0.12 refused", "connect to <ip> refused")]
    [InlineData("No match for “The Bear” in TVmaze", "No match for \"…\" in TVmaze")]
    [InlineData("mail dan@example.com about it", "mail <email> about it")]
    [InlineData("Transcode segment 14 failed: exit code 1", "Transcode segment 14 failed: exit code 1")]
    public void ErrorReportsKeepNothingAboutTheUser(string message, string expected) => Assert.Equal(expected, ErrorReports.Scrub(message));

    [Fact]
    public async Task ErrorReportsAreOffUntilTurnedOnAndTheAppsFollow()
    {
        var admin = Admin();
        admin.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");

        var before = await admin.GetFromJsonAsync<JsonElement>(Api("/system/status"));
        var settings = await admin.GetFromJsonAsync<JsonElement>(Api("/logs/settings"));
        Assert.False(before.GetProperty("errorReports").GetProperty("enabled").GetBoolean());
        Assert.False(settings.GetProperty("errorReports").GetBoolean());

        try
        {
            await admin.PutAsJsonAsync(Api("/logs/settings"), new { errorReports = true });
            var on = await admin.GetFromJsonAsync<JsonElement>(Api("/system/status"));
            var id = on.GetProperty("errorReports").GetProperty("installId").GetString();
            Assert.True(on.GetProperty("errorReports").GetProperty("enabled").GetBoolean());
            Assert.Matches("^[0-9a-f]{32}$", id);

            await admin.PutAsJsonAsync(Api("/logs/settings"), new { errorReports = false });
            var off = await admin.GetFromJsonAsync<JsonElement>(Api("/system/status"));
            Assert.False(off.GetProperty("errorReports").GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, off.GetProperty("errorReports").GetProperty("installId").ValueKind);
        }
        finally
        {
            await admin.PutAsJsonAsync(Api("/logs/settings"), new { errorReports = false });
        }
    }

    [Fact]
    public async Task OnlyAdminsSwitchErrorReports()
    {
        var device = await DeviceAsync("Not an admin TV");
        await device.GetAsync(Api("/system/status"));

        // A device sign-in is the main profile unless it picked another; a profile that isn't main can't manage the server.
        var profile = factory.CreateClient();
        var sent = await profile.PutAsJsonAsync(Api("/logs/settings"), new { errorReports = true });

        Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);
    }

    private async Task<HttpClient> DeviceAsync(string name)
    {
        var response = await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = name });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("token").GetString()!);
        return client;
    }

    /// <summary>The newest entry mentioning <paramref name="text"/>, once the file sink has written it.</summary>
    private async Task<JsonElement> WaitForAsync(string text)
    {
        for (var i = 0; i < 50; i++)
        {
            var page = await Admin().GetFromJsonAsync<JsonElement>(Api($"/logs?q={text}"));
            if (page.GetProperty("entries").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } entry)
            {
                return entry;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Nothing mentioning {text} was logged.");
    }
}
