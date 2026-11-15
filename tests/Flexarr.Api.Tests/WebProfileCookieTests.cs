using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// The web app's chosen profile lives in an HttpOnly cookie that only the web app's own requests (X-Requested-With)
/// read, rather than in the page's storage.
/// </summary>
public class WebProfileCookieTests
{
    private const string Cookie = "flexarr.profile";

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task<string> AddProfileAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var sam = new Profile { Id = Profile.NewId(), Name = "Sam" };
        db.Profiles.Add(sam);
        await db.SaveChangesAsync();
        return sam.Id;
    }

    private static HttpClient Client(FlexarrApiFactory server, bool webApp, string? cookie = null, string? header = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (webApp)
        {
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{Cookie}={cookie}");
        }

        if (header is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", header);
        }

        return client;
    }

    /// <summary>The profile cookie a response sets: its value, "" when it's deleted, nil when untouched.</summary>
    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith($"{Cookie}=", StringComparison.Ordinal)).Select(v => v[(Cookie.Length + 1)..].Split(';')[0]).LastOrDefault()
            : null;

    private static async Task<string> ProfileIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString()!;

    [Fact]
    public async Task ChoosingInTheWebApp_KeepsTheProfileInAnHttpOnlyCookie_ThatOnlyTheWebAppsRequestsRead()
    {
        using var server = new FlexarrApiFactory();
        var sam = await AddProfileAsync(server.Services);

        var chosen = await Client(server, webApp: true).PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { });
        chosen.EnsureSuccessStatusCode();
        var token = SetCookie(chosen);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Contains(chosen.Headers.GetValues("Set-Cookie"), v => v.StartsWith($"{Cookie}=", StringComparison.Ordinal) && v.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(sam, await ProfileIdAsync(Client(server, webApp: true, cookie: token)));
        // Anything else carrying the cookie (an app's shared cookie store, another site's page) isn't the web app.
        Assert.Equal(PlaybackState.DefaultUser, await ProfileIdAsync(Client(server, webApp: false, cookie: token)));

        // Choosing the main profile forgets it, as signing out does.
        var main = await Client(server, webApp: true, cookie: token).PostAsJsonAsync(Api($"/profiles/{PlaybackState.DefaultUser}/select"), new { });
        Assert.Equal("", SetCookie(main));
        Assert.Equal("", SetCookie(await Client(server, webApp: true, cookie: token).DeleteAsync(Api("/session"))));
    }

    [Fact]
    public async Task AWebPageFromBefore_SendingTheTokenInTheHeader_HasItMovedIntoTheCookie()
    {
        using var server = new FlexarrApiFactory();
        var sam = await AddProfileAsync(server.Services);
        var token = (await (await Client(server, webApp: false).PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var me = await Client(server, webApp: true, header: token).GetAsync(Api("/me"));
        Assert.Equal(token, SetCookie(me));
        // An app sending the header is never given a cookie.
        Assert.Null(SetCookie(await Client(server, webApp: false, header: token).GetAsync(Api("/me"))));
        // Nor is a page that already has it.
        Assert.Null(SetCookie(await Client(server, webApp: true, cookie: token, header: token).GetAsync(Api("/me"))));
        // A token that no longer verifies, in the cookie, is ignored.
        Assert.Equal(PlaybackState.DefaultUser, await ProfileIdAsync(Client(server, webApp: true, cookie: $"{sam}.wrong")));
    }
}
