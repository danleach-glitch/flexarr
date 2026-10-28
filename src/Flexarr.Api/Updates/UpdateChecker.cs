using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Updates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>The newest Flexarr release, as GitHub describes it.</summary>
public sealed record LatestRelease(ReleaseVersion Version, string Notes, Uri Page, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The Mac build for this machine's processor, which scripts/release.sh attaches to every release.</summary>
    public ReleaseAsset? MacBuild(System.Runtime.InteropServices.Architecture architecture) =>
        Assets.FirstOrDefault(a => a.Name == $"flexarr-{Version}-{UpdateChecker.MacRuntime(architecture)}.tar.gz");

    /// <summary>The SHA-256 of every build, one "&lt;hash&gt;  &lt;name&gt;" line each.</summary>
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name == "SHA256SUMS");
}

/// <summary>
/// Asks GitHub for Flexarr's latest release (one unauthenticated request; drafts and pre-releases never count) and
/// remembers the answer until the next check.
/// </summary>
public sealed partial class UpdateChecker(IHttpClientFactory clients, IOptionsMonitor<FlexarrOptions> options, TimeProvider time, ILogger<UpdateChecker> logger) : IDisposable
{
    /// <summary>The HTTP client for GitHub's release lookups.</summary>
    public const string ClientName = "updates";

    /// <summary>The HTTP client for a release's files: a build is tens of megabytes, on whatever connection the server has.</summary>
    public const string DownloadClientName = "update-downloads";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public LatestRelease? Latest { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Why the last check didn't get an answer; null after one that did.</summary>
    public string? CheckError { get; private set; }

    /// <summary>A newer release than the one running. Never for a development build: main is usually ahead of it.</summary>
    public bool Available(ReleaseVersion current) => !current.IsDevelopment && Latest is { } latest && latest.Version > current;

    public static string MacRuntime(System.Runtime.InteropServices.Architecture architecture) =>
        architecture == System.Runtime.InteropServices.Architecture.X64 ? "osx-x64" : "osx-arm64";

    public async Task<LatestRelease?> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, options.CurrentValue.Updates.ReleasesUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"Flexarr/{ReleaseVersion.Current} (+https://github.com/danleach-glitch/flexarr)");
            using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
            LastChecked = time.GetUtcNow();
            if (!response.IsSuccessStatusCode)
            {
                CheckError = $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                LogCheckFailed(CheckError);
                return Latest;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
            if (release is null || release.Draft || release.Prerelease || ReleaseVersion.Parse(release.TagName) is not { IsDevelopment: false } version
                || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page))
            {
                CheckError = "GitHub's answer didn't describe a release.";
                LogCheckFailed(CheckError);
                return Latest;
            }

            Latest = new LatestRelease(version, release.Body ?? "", page, release.PublishedAt,
                [.. (release.Assets ?? []).Where(a => Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
                    .Select(a => new ReleaseAsset(a.Name, new Uri(a.BrowserDownloadUrl), a.Size))]);
            CheckError = null;
            LogChecked(version.ToString());
            return Latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            LastChecked = time.GetUtcNow();
            CheckError = ex is TaskCanceledException ? "GitHub didn't answer in time." : $"Couldn't reach GitHub: {ex.Message}";
            LogCheckFailed(CheckError);
            return Latest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked for updates: the latest release is {Version}")]
    private partial void LogChecked(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check for updates: {Reason}")]
    private partial void LogCheckFailed(string reason);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}
