using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Backups;

namespace Flexarr.Api.Tests;

/// <summary>A one-use download link that carries a passphrase, so a protected backup streams to disk (audit perf-68).</summary>
public class BackupPassphraseLinkTests
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task<(HttpClient Browser, string Name)> SignedInBrowserAsync(FlexarrApiFactory factory)
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        await admin.PostAsJsonAsync(Api("/admin/pin"), new { pin = "5319" });
        var name = (await (await admin.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
        var browser = factory.CreateClient();
        using var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session"));
        signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        browser.DefaultRequestHeaders.Add("Cookie", Assert.Single((await browser.SendAsync(signIn)).Headers.GetValues("Set-Cookie")).Split(';')[0]);
        browser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        return (browser, name);
    }

    [Fact]
    public async Task A_link_bought_with_a_passphrase_downloads_the_backup_protected_once()
    {
        await using var factory = new FlexarrApiFactory();
        var (browser, name) = await SignedInBrowserAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await browser.PostAsJsonAsync(Api($"/backups/{name}/download-link"), new { passphrase = "correct horse" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsJsonAsync(Api($"/backups/{name}/download-link"), new { pin = "5319", passphrase = "short" })).StatusCode);

        var link = await (await browser.PostAsJsonAsync(Api($"/backups/{name}/download-link"), new { pin = "5319", passphrase = "correct horse" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var url = link.GetProperty("url").GetString()!;
        Assert.DoesNotContain("correct", url, StringComparison.Ordinal);
        var download = await browser.GetAsync(new Uri(url, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".flexarr-backup", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);

        // It opens with the passphrase into the same backup the plain download gives.
        using var encrypted = new MemoryStream(await download.Content.ReadAsByteArrayAsync());
        using var decrypted = new MemoryStream();
        await BackupCrypto.DecryptAsync(encrypted, decrypted, "correct horse", CancellationToken.None);
        var plain = await (await browser.PostAsJsonAsync(Api($"/backups/{name}/download"), new { pin = "5319" })).Content.ReadAsByteArrayAsync();
        Assert.Equal(plain, decrypted.ToArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_link_without_a_passphrase_is_the_plain_backup_as_before()
    {
        await using var factory = new FlexarrApiFactory();
        var (browser, name) = await SignedInBrowserAsync(factory);

        var link = await (await browser.PostAsJsonAsync(Api($"/backups/{name}/download-link"), new { pin = "5319" })).Content.ReadFromJsonAsync<JsonElement>();
        var download = await browser.GetAsync(new Uri(link.GetProperty("url").GetString()!, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(name, download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }
}
