using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A server with the home-network shortcut on, where tests choose which address each request comes from.</summary>
public sealed class RemoteAccessFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("flexarr-remote-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Flexarr:ApiKey", FlexarrApiFactory.ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:AuthenticationRequired", "DisabledForLocalAddresses");
        builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFromHeader>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    /// <summary>Sets the connecting address from X-Test-Peer, before any of Flexarr's middleware runs.</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext http, RequestDelegate n) =>
            {
                if (http.Request.Headers["X-Test-Peer"].FirstOrDefault() is { } peer)
                {
                    http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                }

                return n(http);
            });
            next(app);
        };
    }
}

public class RemoteAccessTests(RemoteAccessFactory factory) : IClassFixture<RemoteAccessFactory>
{
    private const string Status = "/api/v1/system/status";

    private async Task<HttpStatusCode> GetAsync(string path, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return (await factory.CreateClient().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task HomeNetwork_IsLetIn_WithoutAKey() =>
        Assert.Equal(HttpStatusCode.OK, await GetAsync(Status, ("X-Test-Peer", "192.168.1.83")));

    [Theory]
    [InlineData("CF-Connecting-IP", "203.0.113.7")]
    [InlineData("Tailscale-Funnel-Request", "?1")]
    public async Task TunnelledInternetTraffic_IsNotTreatedAsHome(string header, string value) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", "127.0.0.1"), (header, value)));

    [Fact]
    public async Task ForwardedFromAPublicAddress_ThroughTheLocalProxy_IsNotHome() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", "203.0.113.7")));

    [Fact]
    public async Task TailscaleIdentity_ViaTailscaleServe_SignsInAsTheLinkedProfile()
    {
        await LinkAsync("claire@example.com", "Claire");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profiles");
        request.Headers.Add("X-Test-Peer", "127.0.0.1");
        request.Headers.Add("X-Forwarded-For", "100.70.1.2");
        request.Headers.Add("Tailscale-User-Login", "claire@example.com");
        var response = await factory.CreateClient().SendAsync(request);
        var profiles = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Claire", profiles.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnlinkedTailscaleAccount_IsRefused() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status,
            ("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", "100.70.1.3"), ("Tailscale-User-Login", "stranger@example.com")));

    [Fact]
    public async Task TailscaleHeader_FromALanDevice_IsIgnored()
    {
        await LinkAsync("dan@example.com", null);

        // A LAN device can't claim to be someone over Tailscale; it just gets the home-network rule (main profile).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profiles");
        request.Headers.Add("X-Test-Peer", "192.168.1.50");
        request.Headers.Add("Tailscale-User-Login", "claire-forged@example.com");
        var profiles = await (await factory.CreateClient().SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(profiles.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("isMain").GetBoolean());
    }

    [Fact]
    public async Task OnlyTheMainProfile_CanChangeSettings()
    {
        await LinkAsync("kid@example.com", "Kid");
        var tailnet = new[] { ("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", "100.70.1.4"), ("Tailscale-User-Login", "kid@example.com") };

        Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/libraries", tailnet));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", tailnet));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings/api-key", tailnet));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/filesystem", tailnet));
    }

    /// <summary>Links a Tailscale login to a new profile, or to the main profile when no name is given.</summary>
    private async Task LinkAsync(string login, string? profileName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = profileName is null
            ? await db.Profiles.FindAsync(PlaybackState.DefaultUser)
            : db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = profileName }).Entity;
        profile!.TailscaleLogin = login;
        await db.SaveChangesAsync();
    }
}
