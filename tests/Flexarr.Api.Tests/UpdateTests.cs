using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Updates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class UpdateTests
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Theory]
    [InlineData("0.51.0", "0.51.0", null)]
    [InlineData("v0.51.0", "0.51.0", null)]
    [InlineData("0.51.0+1a2b3c4d", "0.51.0", null)]
    [InlineData("0.51.0-dev.1a2b3c4d+1a2b3c4d", "0.51.0", "dev.1a2b3c4d")]
    public void ReleaseVersion_Parses_WithoutTheCommit(string text, string number, string? suffix)
    {
        var version = ReleaseVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(Version.Parse(number), version.Number);
        Assert.Equal(suffix, version.Suffix);
        Assert.Equal(suffix is not null, version.IsDevelopment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("0.51")]
    [InlineData("0.51.0.1")]
    [InlineData("0.51.0-")]
    public void ReleaseVersion_RefusesWhatIsNotOne(string text) => Assert.Null(ReleaseVersion.Parse(text));

    [Fact]
    public void ReleaseVersion_OrdersByNumber_WithADevelopmentBuildBeforeItsRelease()
    {
        var parse = (string text) => ReleaseVersion.Parse(text)!;

        Assert.True(parse("0.51.0") > parse("0.50.9"));
        Assert.True(parse("0.10.0") > parse("0.9.0"));
        Assert.True(parse("0.51.0") > parse("0.51.0-dev.abc"));
        Assert.True(parse("1.0.0") > parse("0.99.0"));
        Assert.Equal(parse("0.51.0"), parse("v0.51.0+abc"));
    }

    [Fact]
    public void ChecksumFor_FindsTheBuildsLine()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  flexarr-0.51.0-osx-arm64.tar.gz\n{b} *flexarr-0.51.0-osx-x64.tar.gz\n";

        Assert.Equal(a, MacUpdater.ChecksumFor(sums, "flexarr-0.51.0-osx-arm64.tar.gz"));
        Assert.Equal(b.ToLowerInvariant(), MacUpdater.ChecksumFor(sums, "flexarr-0.51.0-osx-x64.tar.gz"));
        Assert.Null(MacUpdater.ChecksumFor(sums, "flexarr-0.51.0-linux-x64.tar.gz"));
        Assert.Null(MacUpdater.ChecksumFor("nothex  flexarr.tar.gz", "flexarr.tar.gz"));
    }

    [Fact]
    public void MissingRuntime_NamesAFrameworkThatIsNotInstalled_AndAcceptsANewerPatch()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-runtime-").FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var dotnet = Path.Combine(root, "dotnet");
            Directory.CreateDirectory(Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", "10.0.3"));
            Directory.CreateDirectory(Path.Combine(dotnet, "shared", "Microsoft.AspNetCore.App", "10.0.3"));
            File.WriteAllText(Path.Combine(app, "Flexarr.runtimeconfig.json"), """
                {"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}
                """);

            Assert.Null(MacUpdater.MissingRuntime(app, dotnet));

            File.WriteAllText(Path.Combine(app, "Flexarr.runtimeconfig.json"), """
                {"runtimeOptions":{"tfm":"net11.0","framework":{"name":"Microsoft.AspNetCore.App","version":"11.0.0"}}}
                """);

            Assert.Equal("ASP.NET Core 11.0", MacUpdater.MissingRuntime(app, dotnet));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepRecentWebFiles_CopiesOnlyRecentFilesTheNewBuildLacks()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-assets-").FullName;
        try
        {
            var from = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            var to = Directory.CreateDirectory(Path.Combine(root, "new")).FullName;
            File.WriteAllText(Path.Combine(from, "index-old.js"), "old");
            File.WriteAllText(Path.Combine(from, "ancient.js"), "ancient");
            File.SetLastWriteTimeUtc(Path.Combine(from, "ancient.js"), DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(Path.Combine(from, "shared.css"), "old build's");
            File.WriteAllText(Path.Combine(to, "shared.css"), "new build's");

            MacUpdater.KeepRecentWebFiles(from, to, DateTime.UtcNow);

            Assert.Equal("old", File.ReadAllText(Path.Combine(to, "index-old.js")));
            Assert.False(File.Exists(Path.Combine(to, "ancient.js")));
            Assert.Equal("new build's", File.ReadAllText(Path.Combine(to, "shared.css")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(3, 59, false)]
    [InlineData(4, 0, true)]
    [InlineData(4, 59, true)]
    [InlineData(5, 0, false)]
    public void UpdateWorker_InstallsOnlyBetweenFourAndFive(int hour, int minute, bool inside) =>
        Assert.Equal(inside, UpdateWorker.InWindow(new TimeSpan(hour, minute, 0)));

    [Fact]
    public async Task Updates_ShowsTheLatestRelease_ButAServerThatCannotUpdateItselfWontInstall()
    {
        using var factory = new FlexarrApiFactory();
        var github = new FakeGitHub("v99.0.0");
        using var server = Serve(factory, github, InstallKind.Docker);
        using var client = Client(server);

        var updates = await client.GetFromJsonAsync<JsonElement>(Api("/updates"));
        var install = await client.PostAsync(Api("/updates/install"), null);

        Assert.Equal("99.0.0", updates.GetProperty("latest").GetProperty("version").GetString());
        Assert.Equal("Docker", updates.GetProperty("install").GetString());
        Assert.True(updates.GetProperty("available").GetBoolean());
        Assert.False(updates.GetProperty("canInstall").GetBoolean());
        Assert.Equal("### Web\n- Something new", updates.GetProperty("latest").GetProperty("notes").GetString());
        Assert.Equal(HttpStatusCode.Conflict, install.StatusCode);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task Updates_AnOlderReleaseIsNotOffered()
    {
        using var factory = new FlexarrApiFactory();
        using var server = Serve(factory, new FakeGitHub("v0.0.1"), InstallKind.MacLaunchAgent);
        using var client = Client(server);

        var updates = await client.GetFromJsonAsync<JsonElement>(Api("/updates"));

        Assert.False(updates.GetProperty("available").GetBoolean());
        Assert.False(updates.GetProperty("canInstall").GetBoolean());
        Assert.Equal(0, updates.GetProperty("inUse").GetArrayLength());
    }

    [Fact]
    public async Task Updates_GitHubFailing_IsReportedAndTheServerCarriesOn()
    {
        using var factory = new FlexarrApiFactory();
        using var server = Serve(factory, new FakeGitHub(null), InstallKind.Other);
        using var client = Client(server);

        var updates = await (await client.PostAsync(Api("/updates/check"), null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, updates.GetProperty("latest").ValueKind);
        Assert.Contains("503", updates.GetProperty("checkError").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, updates.GetProperty("lastChecked").ValueKind);
    }

    [Fact]
    public async Task AutomaticUpdates_AreOffToBeginWith_AndSaved()
    {
        using var factory = new FlexarrApiFactory();
        using var server = Serve(factory, new FakeGitHub("v0.0.1"), InstallKind.MacLaunchAgent);
        using var client = Client(server);

        var before = await client.GetFromJsonAsync<JsonElement>(Api("/updates"));
        var on = await (await client.PutAsJsonAsync(Api("/updates/settings"), new { automatic = true })).Content.ReadFromJsonAsync<JsonElement>();
        var off = await (await client.PutAsJsonAsync(Api("/updates/settings"), new { automatic = false })).Content.ReadFromJsonAsync<JsonElement>();
        var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, "config.json"))).RootElement;

        Assert.False(before.GetProperty("automatic").GetBoolean());
        Assert.True(on.GetProperty("automatic").GetBoolean());
        Assert.False(off.GetProperty("automatic").GetBoolean());
        Assert.False(config.GetProperty("updates").GetProperty("automatic").GetBoolean());
    }

    [Fact]
    public async Task Updates_AreForTheMainProfileOnly()
    {
        using var factory = new FlexarrApiFactory();
        using var server = Serve(factory, new FakeGitHub("v99.0.0"), InstallKind.Other);
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Api("/updates"))).StatusCode);
    }

    private static HttpClient Client(WebApplicationFactory<Program> server)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static WebApplicationFactory<Program> Serve(FlexarrApiFactory factory, FakeGitHub github, InstallKind kind) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddHttpClient(UpdateChecker.ClientName).ConfigurePrimaryHttpMessageHandler(() => github);
            s.AddSingleton<IInstallEnvironment>(new FakeInstall(kind));
        }));

    private sealed record FakeInstall(InstallKind Kind) : IInstallEnvironment
    {
        public string? Root => null;

        public string? DotnetRoot => null;
    }

    /// <summary>GitHub's latest-release answer for <paramref name="tag"/>; a null tag is GitHub being down.</summary>
    private sealed class FakeGitHub(string? tag) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.ToString());
            }

            if (tag is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var version = tag.TrimStart('v');
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tag_name"] = tag,
                ["html_url"] = $"https://github.com/danleach-glitch/flexarr/releases/tag/{tag}",
                ["body"] = "### Web\n- Something new",
                ["published_at"] = "2026-10-28T12:00:00Z",
                ["draft"] = false,
                ["prerelease"] = false,
                ["assets"] = new[]
                {
                    new Dictionary<string, object> { ["name"] = $"flexarr-{version}-osx-arm64.tar.gz", ["browser_download_url"] = "https://example.invalid/a.tar.gz", ["size"] = 10 },
                    new Dictionary<string, object> { ["name"] = "SHA256SUMS", ["browser_download_url"] = "https://example.invalid/SHA256SUMS", ["size"] = 100 },
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
