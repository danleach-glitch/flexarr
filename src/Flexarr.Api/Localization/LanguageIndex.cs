using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Core.Localization;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Localization;

/// <summary>One language in flexarr.io's index (site/languages/index.json, written by make strings).</summary>
public sealed record LanguageIndexEntry(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("coverage")] Dictionary<string, double> Coverage);

internal sealed record LanguageIndexFile(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("languages")] List<LanguageIndexEntry> Languages);

/// <summary>
/// The packs published at flexarr.io/languages (Languages:IndexUrl), and the ones this server downloaded from there into
/// &lt;data&gt;/languages (docs/languages.md, *Built in and downloaded*). The index is read at most once a day and kept on
/// disk, so an offline server still knows what it had; a pack is downloaded the first time someone asks for its
/// language, checked against the index's size and hash and read through <see cref="LanguagePack.Read"/>, then kept and
/// refreshed when the index has a newer version. Any valid pack in that folder is used, which is how the pseudo-language
/// and an admin's own pack get in.
/// </summary>
public sealed partial class LanguageIndex(
    IHttpClientFactory clients,
    IOptionsMonitor<FlexarrOptions> options,
    TimeProvider clock,
    ILogger<LanguageIndex> logger) : IDisposable
{
    public const string ClientName = "languages";
    public static readonly TimeSpan KeptFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<LanguageIndexEntry> _entries = [];
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _failedAt = DateTimeOffset.MinValue;

    public LanguageCatalog Catalog { get; init; } = Loc.Catalog;

    public void Dispose() => _gate.Dispose();

    private string Folder => Path.Combine(options.CurrentValue.DataDirectory, "languages");

    private string IndexFile => Path.Combine(Folder, "index.json");

    /// <summary>The published languages, from the last index this server read (fresh or not); empty when it has none.</summary>
    public IReadOnlyList<LanguageIndexEntry> Entries => _entries;

    /// <summary>Loads the packs kept in the data folder and the last index read. Called once at startup.</summary>
    public void LoadKept()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            if (Path.GetFileName(file) == "index.json")
            {
                continue;
            }

            try
            {
                Catalog.Install(LanguagePack.Read(File.ReadAllBytes(file)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
            {
                LogBadPack(Path.GetFileName(file), e.Message);
            }
        }

        try
        {
            if (File.Exists(IndexFile))
            {
                _entries = Parse(File.ReadAllBytes(IndexFile));
                _checkedAt = File.GetLastWriteTimeUtc(IndexFile);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        {
            LogBadIndex(e.Message);
        }
    }

    /// <summary>Reads the index again when the copy kept is over a day old (and the last try didn't just fail).</summary>
    public async Task<IReadOnlyList<LanguageIndexEntry>> RefreshAsync(CancellationToken ct)
    {
        var url = options.CurrentValue.Languages.IndexUrl;
        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(url) || now - _checkedAt < KeptFor || now - _failedAt < RetryAfter)
        {
            return _entries;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (now - _checkedAt < KeptFor)
            {
                return _entries;
            }

            try
            {
                var bytes = await clients.CreateClient(ClientName).GetByteArrayAsync(url, ct);
                _entries = Parse(bytes);
                _checkedAt = now;
                Directory.CreateDirectory(Folder);
                await WriteAtomicallyAsync(IndexFile, bytes, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException
                                          && !ct.IsCancellationRequested)
            {
                _failedAt = now;
                LogIndexFailed(url, e.Message);
            }

            return _entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes sure the newest published pack for a language is here (downloading it when the index has one newer than the
    /// server's), and returns the pack to use: the newest of built-in, kept and downloaded. Null when there's none.
    /// </summary>
    public async Task<LanguagePack?> EnsureAsync(string code, CancellationToken ct)
    {
        await RefreshAsync(ct);
        var have = Catalog.Find(code);
        var entry = LanguageCode.Fallbacks(code).Select(c => _entries.FirstOrDefault(e => e.Code == c)).FirstOrDefault(e => e is not null);
        if (entry is null || (have is not null && have.Code == entry.Code && have.Version >= entry.Version))
        {
            return have;
        }

        await DownloadAsync(entry, ct);
        return Catalog.Find(code);
    }

    /// <summary>Downloads newer versions of the languages this server already keeps (the daily refresh).</summary>
    public async Task UpdateKeptAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        foreach (var entry in _entries)
        {
            if (File.Exists(PackFile(entry.Code)) && (Catalog.Find(entry.Code) is not { } have || have.Code != entry.Code || have.Version < entry.Version))
            {
                await DownloadAsync(entry, ct);
            }
        }
    }

    private string PackFile(string code) => Path.Combine(Folder, code + ".json");

    private async Task DownloadAsync(LanguageIndexEntry entry, CancellationToken ct)
    {
        var url = options.CurrentValue.Languages.IndexUrl;
        if (string.IsNullOrWhiteSpace(url) || !LanguageCode.IsValid(entry.Code) || entry.Size > LanguagePack.MaxBytes)
        {
            return;
        }

        try
        {
            var packUrl = new Uri(new Uri(url), entry.Code + ".json");
            var bytes = await clients.CreateClient(ClientName).GetByteArrayAsync(packUrl, ct);
            if (bytes.Length != entry.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal))
            {
                LogBadPack(entry.Code, "its size or hash doesn't match the index");
                return;
            }

            var pack = LanguagePack.Read(bytes);
            if (pack.Code != entry.Code)
            {
                LogBadPack(entry.Code, $"it says it's {pack.Code}");
                return;
            }

            Directory.CreateDirectory(Folder);
            await WriteAtomicallyAsync(PackFile(entry.Code), bytes, ct);
            if (Catalog.Install(pack))
            {
                LogInstalled(pack.Code, pack.Version);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException
                                      && !ct.IsCancellationRequested)
        {
            LogPackFailed(entry.Code, e.Message);
        }
    }

    private static List<LanguageIndexEntry> Parse(byte[] bytes)
    {
        var file = JsonSerializer.Deserialize<LanguageIndexFile>(bytes, Json) ?? throw new InvalidDataException("Empty index");
        if (file.Format != LanguagePack.Format)
        {
            throw new InvalidDataException($"Index format {file.Format} isn't one this server reads");
        }

        return file.Languages.Where(e => LanguageCode.IsValid(e.Code) && e.Direction is "ltr" or "rtl").ToList();
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".part";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Language pack {Code} version {Version} is in use")]
    private partial void LogInstalled(string code, long version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the language index from {Url}: {Reason}")]
    private partial void LogIndexFailed(string url, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't download language pack {Code}: {Reason}")]
    private partial void LogPackFailed(string code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored language pack {Name}: {Reason}")]
    private partial void LogBadPack(string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored the kept language index: {Reason}")]
    private partial void LogBadIndex(string reason);
}

/// <summary>Loads the kept packs at startup, then refreshes the index and the kept packs once a day.</summary>
public sealed class LanguageWorker(LanguageIndex index, TimeProvider time) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        index.LoadKept();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await index.UpdateKeptAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never stop the server over language packs.
                }

                await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
