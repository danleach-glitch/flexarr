using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Flexarr.Api.Backups;

public sealed record SettingsFolderDto(string Path, bool Exists);

/// <param name="AlreadyHere">This server has a library of that name, so it'd be left as it is.</param>
public sealed record SettingsFileLibraryDto(string Name, LibraryType Type, IReadOnlyList<SettingsFolderDto> Folders, bool AlreadyHere);

/// <summary>What a settings file holds: the sections in it, and its libraries.</summary>
public sealed record SettingsFileDto(DateTime? ExportedAt, string? FlexarrVersion, IReadOnlyList<string> Sections, IReadOnlyList<SettingsFileLibraryDto> Libraries);

/// <param name="Folders">Where the library's folders are on this server (changed from the file's when they moved).</param>
public sealed record SettingsLibraryImport(string Name, IReadOnlyList<string> Folders);

/// <param name="Pin">The admin PIN, for the server keys section.</param>
/// <param name="Confirm">The person said yes, for the server keys section when the main profile has no PIN.</param>
public sealed record SettingsImportRequest(IReadOnlyList<string> Sections, IReadOnlyList<SettingsLibraryImport>? Libraries = null, string? Pin = null,
    bool Confirm = false);

/// <param name="Problems">Libraries that weren't added, and why.</param>
public sealed record SettingsImportResult(IReadOnlyList<string> Applied, int LibrariesAdded, IReadOnlyList<string> Problems);

/// <summary>
/// Settings files: the server's settings (config.json) by section, and its libraries, to carry to another server or
/// to keep. Unlike a backup, there's no database: no profiles, nothing watched.
/// </summary>
public static class SettingsTransfer
{
    public const string FormatName = "flexarr-settings";
    public const string LibrariesSection = "libraries";
    public const string KeysSection = "keys";
    public const string MachineSection = "machine";

    /// <summary>The biggest settings file opened: a real one is a few kilobytes.</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;

    private const int MaxLibraries = 100;

    /// <summary>The settings in each section, as config.json names them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Sections = new Dictionary<string, string[]>
    {
        ["connections"] = ["arr", "seerr", "bazarr", "tmdb", "trakt", "pathMappings"],
        ["liveTv"] = ["liveTv"],
        ["streaming"] = ["transcoding"],
        ["layout"] = ["layout"],
        ["podcasts"] = ["podcasts"],
        ["notifications"] = ["notifications"],
        // Not family invites: their Tailscale OAuth client can mint keys into this tailnet, and the policy they rely
        // on names this server's own address, so another server sets them up afresh.
        [AccessSection] = ["authenticationRequired", "trustedProxies", "knownHosts", "loopbackNeedsForwarding"],
        // Where things are on this machine: rarely right on another.
        [MachineSection] = ["ffmpegPath", "ffprobePath", "cacheDirectory", "backups"],
        [KeysSection] = ["apiKey", "webhookSecret", "imageSecret"],
    };

    /// <summary>Settings added to a section later: a file without them leaves this server's as they are.</summary>
    private static readonly HashSet<string> KeptUnlessGiven = new(StringComparer.OrdinalIgnoreCase) { "trakt", "tmdb" };

    /// <summary>
    /// What of a setting goes between servers: for Trakt, only the app's id and secret, never where its token calls go
    /// (a file can't send everyone's Trakt sign-ins to another address).
    /// </summary>
    private static JsonNode? Portable(string key, JsonNode? value)
    {
        if (!key.Equals("trakt", StringComparison.OrdinalIgnoreCase) || value is not JsonObject trakt)
        {
            return value;
        }

        var app = new JsonObject();
        foreach (var name in new[] { "clientId", "clientSecret" })
        {
            if (trakt.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { } v)
            {
                app[name] = v.DeepClone();
            }
        }

        return app;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Sections whose settings decide who gets in, or what the server runs and where it writes (ffmpeg's path): taking
    /// them from a file asks for the admin PIN, as showing the API key does.
    /// </summary>
    public static bool NeedsConfirmation(IEnumerable<string> sections) => sections.Any(s => s is KeysSection or MachineSection or AccessSection);

    public const string AccessSection = "access";

    /// <summary>
    /// A settings file as Flexarr will use it: only the settings it knows, and only libraries it could add, so nothing
    /// else in the file goes any further. Null when it isn't a settings file at all.
    /// </summary>
    public static JsonObject? Clean(JsonNode? file)
    {
        if (file is not JsonObject document || Text(document, "format") != FormatName)
        {
            return null;
        }

        var settings = new JsonObject();
        if (Get(document, "settings") is JsonObject given)
        {
            foreach (var (key, value) in given.ToList())
            {
                // Settings are named as config.json names them; anything else (a "liveTv:password" that the config
                // reader would take apart, a setting this version doesn't have) is left behind.
                if (Sections.Values.SelectMany(k => k).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } known
                    && !settings.ContainsKey(known))
                {
                    settings[known] = value?.DeepClone();
                }
            }
        }

        var libraries = new JsonArray();
        foreach (var library in Libraries(document).DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Take(MaxLibraries))
        {
            libraries.Add(JsonSerializer.SerializeToNode(library, Json));
        }

        return new JsonObject
        {
            ["format"] = FormatName,
            ["exportedAt"] = DateTime.TryParse(Text(document, "exportedAt"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : null,
            ["flexarrVersion"] = Text(document, "flexarrVersion") is { Length: <= 64 } version ? version : null,
            ["settings"] = settings,
            [LibrariesSection] = libraries,
        };
    }

    /// <summary>
    /// Why the server couldn't use these settings (a list where a word should be, a word where a number should), or
    /// null when it can. Written to config.json, such settings would stop the server reading any of them.
    /// </summary>
    public static string? BindProblem(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = new FlexarrOptions();
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(config.ToJsonString()));
            new ConfigurationBuilder().AddJsonStream(stream).Build().Bind(options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return ex.Message;
        }

        if (config.ToJsonString().Contains("\\u0000", StringComparison.Ordinal))
        {
            return "a setting has a null character in it.";
        }

        if (options.Backups.Folder is { } folder && !Path.IsPathFullyQualified(folder))
        {
            return $"the backups folder “{folder}” isn’t a full path.";
        }

        if (options.TrustedProxies.FirstOrDefault(p => System.Net.IPNetwork.TryParse(p, out var network) && network.PrefixLength < 8) is { } everyone)
        {
            // A proxy range that takes in the internet would let anyone say they're on the home network.
            return $"“{everyone}” is far too wide a range to trust as a proxy.";
        }

        foreach (var (name, address) in Addresses(config))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                return $"{name}’s address “{address}” isn’t an http or https address.";
            }
        }

        return null;
    }

    public static async Task<JsonObject> ExportAsync(string dataDirectory, FlexarrDbContext db, DateTime now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var path = ServerConfigStore.PathFor(dataDirectory);
        var config = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject() ?? [] : [];
        var settings = new JsonObject();
        foreach (var (key, value) in config.Where(p => SectionOf(p.Key) is not null))
        {
            settings[key] = Portable(key, value?.DeepClone());
        }

        db.HiddenLibraryIds = [];
        var libraries = (await db.Libraries.AsNoTracking().Include(l => l.Folders).OrderBy(l => l.Name).ToListAsync(ct))
            .Where(l => l.Type != LibraryType.Podcasts && !RecordingsLibrary.Is(l))
            .Select(l => new { l.Name, l.Type, Folders = l.Folders.Select(f => f.Path).Order().ToList(), l.FineForKids });

        return new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = 1,
            ["exportedAt"] = now,
            ["flexarrVersion"] = AppVersion.Current,
            ["settings"] = settings,
            [LibrariesSection] = JsonSerializer.SerializeToNode(libraries, Json),
        };
    }

    public static async Task<SettingsFileDto> DescribeAsync(JsonObject document, FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(db);
        db.HiddenLibraryIds = [];
        var here = await db.Libraries.AsNoTracking().Select(l => l.Name).ToListAsync(ct);
        var libraries = Libraries(document)
            .Select(l => new SettingsFileLibraryDto(l.Name, l.Type, [.. l.Folders.Select(f => new SettingsFolderDto(f, Directory.Exists(f)))],
                here.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var settings = document["settings"] as JsonObject ?? [];
        var sections = settings.Select(p => SectionOf(p.Key)).OfType<string>().Distinct().ToList();
        if (libraries.Count > 0)
        {
            sections.Insert(0, LibrariesSection);
        }

        return new SettingsFileDto(Get(document, "exportedAt") is JsonValue at && at.TryGetValue<DateTime>(out var exportedAt) ? exportedAt : null,
            Text(document, "flexarrVersion"), sections, libraries);
    }

    /// <summary>Puts the chosen sections in place of this server's, and adds the chosen libraries that aren't here yet.</summary>
    public static async Task<SettingsImportResult> ApplyAsync(JsonObject document, SettingsImportRequest request, string dataDirectory, FlexarrDbContext db,
        ILibraryScanQueue queue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(queue);

        var settings = document["settings"] as JsonObject ?? [];
        var chosen = request.Sections.Where(s => s != LibrariesSection).ToHashSet();
        var applied = new List<string>();
        if (chosen.Count > 0)
        {
            // Checked as a whole before it's written: config.json is read again as soon as it changes, and settings
            // the server can't read would stop it reading all of them.
            ServerConfigStore.Update(dataDirectory, config =>
            {
                foreach (var section in chosen.Where(s => settings.Any(p => SectionOf(p.Key) == s)))
                {
                    // The section as the file has it: settings it doesn't have go back to their defaults, except ones a
                    // file from before they were exported can't have had (your own Trakt app, the TMDB key).
                    foreach (var key in config.Select(p => p.Key).Where(k => SectionOf(k) == section && !(KeptUnlessGiven.Contains(k) && !settings.ContainsKey(k))).ToList())
                    {
                        config.Remove(key);
                    }

                    foreach (var (key, value) in settings.Where(p => SectionOf(p.Key) == section))
                    {
                        config[key] = Portable(key, value?.DeepClone());
                    }

                    applied.Add(section);
                }

                if (BindProblem(config) is { } problem)
                {
                    throw new InvalidDataException($"These settings can’t be used here: {problem}");
                }
            });
        }

        var added = 0;
        var problems = new List<string>();
        if (request.Sections.Contains(LibrariesSection) && request.Libraries is { } wanted)
        {
            db.HiddenLibraryIds = [];
            var inFile = Libraries(document).DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            var here = await db.Libraries.Select(l => l.Name).ToListAsync(ct);
            var taken = await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct);
            foreach (var library in wanted)
            {
                if (!inFile.TryGetValue(library.Name, out var original))
                {
                    continue;
                }

                if (original.Type == LibraryType.TvShows && original.Name == RecordingsLibrary.Name)
                {
                    problems.Add($"{RecordingsLibrary.Name} is the name of Live TV’s recordings, so it wasn’t added.");
                    continue;
                }

                if (here.Contains(original.Name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{original.Name} is already here, so it was left as it is.");
                    continue;
                }

                var folders = library.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.GetFullPath(f.Trim()).TrimEnd('/')).Distinct().ToList();
                if (folders.Count == 0)
                {
                    problems.Add($"{original.Name} has no folders.");
                    continue;
                }

                if (folders.FirstOrDefault(f => !Directory.Exists(f)) is { } missing)
                {
                    problems.Add($"{original.Name}: {missing} isn’t on this server.");
                    continue;
                }

                if (folders.FirstOrDefault(f => taken.Any(t => PathComparison.IsUnder(f, t) || PathComparison.IsUnder(t, f))) is { } overlapping)
                {
                    problems.Add($"{original.Name}: {overlapping} overlaps a folder in another library.");
                    continue;
                }

                var created = new Library
                {
                    Name = original.Name, Type = original.Type, Folders = [.. folders.Select(f => new LibraryFolder { Path = f })], FineForKids = original.FineForKids,
                };
                db.Libraries.Add(created);
                await db.SaveChangesAsync(ct);
                foreach (var folder in folders)
                {
                    await queue.EnqueueAsync(new LibraryScanRequest(folder, "Library imported"), ct);
                }

                here.Add(created.Name);
                taken.AddRange(folders);
                added++;
            }

            if (added > 0)
            {
                applied.Insert(0, LibrariesSection);
            }
        }

        return new SettingsImportResult(applied, added, problems);
    }

    /// <summary>
    /// Which section a config.json setting belongs to; null for one that's never carried (this server's own, such as when
    /// sign-ins from older versions stop working here, or one this version doesn't know).
    /// </summary>
    public static string? SectionOf(string key)
    {
        foreach (var (section, keys) in Sections)
        {
            if (keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>
    /// The settings a restored backup brings: every one Flexarr knows, under its own name, from the backup, except this
    /// machine's paths (ffmpeg, the cache, the backups and logs folders) and its own bookkeeping, which stay as they are
    /// here. Paths belong to wherever the backup was made, and a backup someone else made could point ffmpeg's at any
    /// program; anything else in the file (a "dataDirectory", a "backups:folder" the config reader would take apart, a
    /// setting this version doesn't have) is left behind. The server's id comes with the backup, so apps signed in to
    /// the server it was made on carry on.
    /// </summary>
    public static void KeepMachinePaths(string restoredConfig, string currentConfig)
    {
        if (!File.Exists(restoredConfig))
        {
            return;
        }

        var backup = JsonNode.Parse(File.ReadAllText(restoredConfig)) as JsonObject ?? [];
        var current = File.Exists(currentConfig) ? JsonNode.Parse(File.ReadAllText(currentConfig)) as JsonObject ?? [] : [];
        var machine = Sections[MachineSection];
        var restored = new JsonObject();
        foreach (var key in Restorable)
        {
            var from = machine.Contains(key, StringComparer.OrdinalIgnoreCase) ? current
                : ServerOwn.Contains(key, StringComparer.OrdinalIgnoreCase) ? (Get(current, key) is null ? backup : current)
                : key == "serverId" && Get(backup, key) is null ? current
                : backup;
            if (Get(from, key) is { } value)
            {
                restored[key] = value.DeepClone();
            }
        }

        // Where the logs are written is this machine's too.
        if (restored["logs"] is JsonObject logs)
        {
            if (logs.FirstOrDefault(p => p.Key.Equals("directory", StringComparison.OrdinalIgnoreCase)).Key is { } directory)
            {
                logs.Remove(directory);
            }

            if (Get(current, "logs") is JsonObject here && Get(here, "directory") is { } folder)
            {
                logs["directory"] = folder.DeepClone();
            }
        }

        ServerConfigStore.WritePrivately(restoredConfig, restored.ToJsonString(Json));
    }

    /// <summary>
    /// The server's own in config.json, kept through a restore: when sign-ins from older versions stop working, and
    /// family invites (their OAuth client and relay port belong to this server's tailnet; guests' sign-ins and
    /// guests.json come back with the backup and need them).
    /// </summary>
    private static readonly string[] ServerOwn = ["legacySignInsUntil", "remoteGuests"];

    /// <summary>
    /// Every setting config.json can hold, by its config.json name: the server's options, but not where its data is
    /// (that's how it found this file) or the revision of its last write.
    /// </summary>
    private static readonly string[] Restorable = typeof(FlexarrOptions).GetProperties()
        .Where(p => p.CanWrite && p.Name is not nameof(FlexarrOptions.DataDirectory) and not nameof(FlexarrOptions.ConfigRevision))
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
        .ToArray();

    /// <summary>The single services a config can point at, by config.json name.</summary>
    private static readonly KeyValuePair<string, string>[] Services = [new("seerr", "Seerr"), new("bazarr", "Bazarr"), new("liveTv", "Live TV")];

    /// <summary>The web addresses in a config: each *arr's, Seerr's, Bazarr's and the Live TV source's.</summary>
    private static IEnumerable<(string Name, string Address)> Addresses(JsonObject config)
    {
        foreach (var (key, value) in config)
        {
            if (key.Equals("arr", StringComparison.OrdinalIgnoreCase) && value is JsonArray arr)
            {
                foreach (var instance in arr.OfType<JsonObject>())
                {
                    if (Text(instance, "baseUrl") is { Length: > 0 } address)
                    {
                        yield return (Text(instance, "name") ?? "A Sonarr or Radarr", address);
                    }
                }
            }
            else if (Services.FirstOrDefault(s => key.Equals(s.Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } named
                && value is JsonObject service && Text(service, "baseUrl") is { Length: > 0 } address)
            {
                yield return (named.Value, address);
            }
        }
    }

    /// <summary>A property, whatever case it's written in.</summary>
    private static JsonNode? Get(JsonObject node, string name) =>
        node.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A property's text, or null when it isn't text.</summary>
    private static string? Text(JsonObject node, string name) =>
        Get(node, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <param name="FineForKids">Everything in it is fine for profiles with an age limit (absent from files before 0.56).</param>
    private sealed record FileLibrary(string Name, LibraryType Type, List<string> Folders, bool FineForKids = false);

    private static readonly JsonSerializerOptions LibraryJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static IEnumerable<FileLibrary> Libraries(JsonObject document)
    {
        try
        {
            return [.. (Get(document, LibrariesSection)?.Deserialize<List<FileLibrary?>>(LibraryJson) ?? [])
                .OfType<FileLibrary>()
                .Where(l => l is { Name.Length: > 0 and <= 200, Folders.Count: > 0 and <= 20 } && !string.IsNullOrWhiteSpace(l.Name)
                    && Enum.IsDefined(l.Type) && l.Type != LibraryType.Podcasts && l.Folders.All(f => f is { Length: > 0 and <= 4096 }))
                .Select(l => l with { Name = l.Name.Trim() })];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }
}
