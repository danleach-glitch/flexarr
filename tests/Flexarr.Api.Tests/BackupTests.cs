using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Backups;
using Flexarr.Api.Configuration;
using Microsoft.Data.Sqlite;

namespace Flexarr.Api.Tests;

public class BackupTests
{
    private static HttpClient Client(FlexarrApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task<JsonElement> UploadAsync(HttpClient client, byte[] file)
    {
        using var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await client.PostAsync(Api("/backups/uploads"), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData((1 << 20) * 2 + 123)]
    public async Task Encryption_RoundTrips(int size)
    {
        var plain = new byte[size];
        Random.Shared.NextBytes(plain);
        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plain), encrypted, "correct horse", CancellationToken.None);
        encrypted.Position = 0;
        using var decrypted = new MemoryStream();

        await BackupCrypto.DecryptAsync(encrypted, decrypted, "correct horse", CancellationToken.None);

        Assert.Equal(plain, decrypted.ToArray());
    }

    [Fact]
    public async Task Encryption_RefusesTheWrongPassphrase_AndAFileCutShort()
    {
        var plain = new byte[(1 << 20) + 5];
        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plain), encrypted, "correct horse", CancellationToken.None);
        var bytes = encrypted.ToArray();
        // Without the last chunk, what's left still decrypts chunk by chunk, but never reaches one marked last.
        var cut = bytes[..^(5 + 5 + 16)];

        await Assert.ThrowsAsync<WrongPassphraseException>(() =>
            BackupCrypto.DecryptAsync(new MemoryStream(bytes), new MemoryStream(), "battery staple", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupCrypto.DecryptAsync(new MemoryStream(cut), new MemoryStream(), "correct horse", CancellationToken.None));
    }

    [Fact]
    public void BackupNames_SayWhatKindAndWhen()
    {
        var at = new DateTime(2026, 10, 27, 3, 30, 0, DateTimeKind.Utc);
        var name = BackupStore.NameFor(BackupKind.PreUpgrade, at);

        Assert.Equal("flexarr-backup-20261027-033000-pre-upgrade.zip", name);
        Assert.Equal((BackupKind.PreUpgrade, at), BackupStore.Parse(name));
        Assert.Null(BackupStore.Parse("../flexarr-backup-20261027-033000-manual.zip"));
        Assert.Null(BackupStore.Parse("flexarr-backup-20261027-033000-sideways.zip"));
    }

    [Fact]
    public void NightlyRunsAtHalfPastThree()
    {
        var evening = new DateTimeOffset(2026, 10, 27, 22, 0, 0, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(5.5), BackupWorker.UntilNext(evening));
        Assert.Equal(TimeSpan.FromHours(2.5), BackupWorker.UntilNext(early));
    }

    [Fact]
    public async Task BackUpNow_SavesTheDatabaseSettingsAndPictures_AndDownloads()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        Directory.CreateDirectory(Path.Combine(factory.DataDirectory, "profile-images"));
        await File.WriteAllBytesAsync(Path.Combine(factory.DataDirectory, "profile-images", "6f776e6572.jpg"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(factory.DataDirectory, "person-images"));
        await File.WriteAllBytesAsync(Path.Combine(factory.DataDirectory, "person-images", "0193a0b1c2d37e4f8a9b0c1d2e3f4a5b.jpg"), [4, 5, 6]);

        var created = await (await client.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>();
        var name = created.GetProperty("name").GetString()!;
        var list = await client.GetFromJsonAsync<JsonElement>(Api("/backups"));
        var download = await client.PostAsJsonAsync(Api($"/backups/{name}/download"), new { });
        using var zip = new ZipArchive(await download.Content.ReadAsStreamAsync());

        Assert.Equal("Manual", created.GetProperty("kind").GetString());
        Assert.Contains(list.GetProperty("backups").EnumerateArray(), b => b.GetProperty("name").GetString() == name);
        Assert.Equal(Path.Combine(factory.DataDirectory, "backups"), list.GetProperty("folder").GetString());
        Assert.Subset(zip.Entries.Select(e => e.FullName).ToHashSet(), new HashSet<string> { "manifest.json", "flexarr.db", "config.json", "profile-images/6f776e6572.jpg", "person-images/0193a0b1c2d37e4f8a9b0c1d2e3f4a5b.jpg" });
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(factory.DataDirectory, "backups", name)));
        }
    }

    [Fact]
    public async Task ProtectedDownload_NeedsItsPassphraseToOpen_ThenRestores()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var name = (await (await client.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        var protectedFile = await (await client.PostAsJsonAsync(Api($"/backups/{name}/download"), new { passphrase = "correct horse" })).Content.ReadAsByteArrayAsync();

        var upload = await UploadAsync(client, protectedFile);
        var id = upload.GetProperty("id").GetString();
        var wrong = await client.PostAsJsonAsync(Api($"/backups/uploads/{id}/open"), new { passphrase = "battery staple" });
        var opened = await (await client.PostAsJsonAsync(Api($"/backups/uploads/{id}/open"), new { passphrase = "correct horse" })).Content.ReadFromJsonAsync<JsonElement>();
        var restore = await client.PostAsJsonAsync(Api($"/backups/uploads/{id}/restore"), new { });

        Assert.True(upload.GetProperty("encrypted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, upload.GetProperty("kind").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("backup", opened.GetProperty("kind").GetString());
        Assert.Equal(1, opened.GetProperty("backup").GetProperty("profiles").GetInt32());
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("backup").GetProperty("problem").ValueKind);
        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        Assert.Equal(1, factory.Restart.Requests);
        // A copy of the server as it was is kept first; the backup waits for the restart.
        var list = await client.GetFromJsonAsync<JsonElement>(Api("/backups"));
        Assert.Contains(list.GetProperty("backups").EnumerateArray(), b => b.GetProperty("kind").GetString() == "PreRestore");
        Assert.True(File.Exists(Path.Combine(PendingRestore.FolderFor(factory.DataDirectory), "flexarr.db")));
    }

    [Fact]
    public async Task ShortPassphrases_AreRefused()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);

        var response = await client.PostAsJsonAsync(Api("/backups/settings-export"), new { passphrase = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BackupFromANewerFlexarr_IsNotRestored()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var name = (await (await client.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        var bytes = await (await client.PostAsJsonAsync(Api($"/backups/{name}/download"), new { })).Content.ReadAsByteArrayAsync();
        var future = await WithMigrationAsync(bytes, "99991231000000_FromTheFuture");

        var upload = await UploadAsync(client, future);
        var restore = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/restore"), new { });

        Assert.Contains("newer Flexarr", upload.GetProperty("backup").GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal(0, factory.Restart.Requests);
    }

    [Fact]
    public async Task UnknownFiles_AreRefused()
    {
        await using var factory = new FlexarrApiFactory();
        using var content = new ByteArrayContent("not a backup"u8.ToArray());

        var response = await Client(factory).PostAsync(Api("/backups/uploads"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void PendingRestore_ReplacesTheDatabaseAndDropsTheOldLog()
    {
        var data = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(data, "flexarr.db"), "old");
            File.WriteAllText(Path.Combine(data, "flexarr.db-wal"), "old log");
            File.WriteAllText(Path.Combine(data, "config.json"), "{\"apiKey\":\"old\"}");
            Directory.CreateDirectory(Path.Combine(data, "profile-images"));
            File.WriteAllText(Path.Combine(data, "profile-images", "gone.jpg"), "");
            var unpacked = Directory.CreateDirectory(Path.Combine(data, "unpacked")).FullName;
            File.WriteAllText(Path.Combine(unpacked, "flexarr.db"), "new");
            File.WriteAllText(Path.Combine(unpacked, "config.json"), "{\"apiKey\":\"new\"}");
            Directory.CreateDirectory(Path.Combine(unpacked, "person-images"));
            File.WriteAllText(Path.Combine(unpacked, "person-images", "0193a0b1c2d37e4f8a9b0c1d2e3f4a5b.jpg"), "photo");

            PendingRestore.Stage(data, unpacked, DateTime.UtcNow);
            var applied = PendingRestore.Apply(data, DateTime.UtcNow);

            Assert.True(applied);
            Assert.Equal("new", File.ReadAllText(Path.Combine(data, "flexarr.db")));
            Assert.False(File.Exists(Path.Combine(data, "flexarr.db-wal")));
            Assert.Contains("new", File.ReadAllText(Path.Combine(data, "config.json")), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(data, "profile-images")));
            Assert.Equal("photo", File.ReadAllText(Path.Combine(data, "person-images", "0193a0b1c2d37e4f8a9b0c1d2e3f4a5b.jpg")));
            Assert.False(Directory.Exists(PendingRestore.FolderFor(data)));
            Assert.False(PendingRestore.Apply(data, DateTime.UtcNow));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task OnlySoManyNightlies_AreKept()
    {
        await using var factory = new FlexarrApiFactory();
        var store = (BackupStore)factory.Services.GetService(typeof(BackupStore))!;
        var folder = Directory.CreateDirectory(Path.Combine(factory.DataDirectory, "backups")).FullName;
        var start = new DateTime(2026, 10, 1, 3, 30, 0, DateTimeKind.Utc);
        for (var day = 0; day < 9; day++)
        {
            File.WriteAllBytes(Path.Combine(folder, BackupStore.NameFor(BackupKind.Nightly, start.AddDays(day))), []);
        }

        File.WriteAllBytes(Path.Combine(folder, BackupStore.NameFor(BackupKind.Manual, start)), []);

        await store.CreateAsync(BackupKind.Nightly, CancellationToken.None);
        var kept = store.List();

        Assert.Equal(7, kept.Count(b => b.Kind == BackupKind.Nightly));
        Assert.Single(kept, b => b.Kind == BackupKind.Manual);
        Assert.DoesNotContain(kept, b => b.Kind == BackupKind.Nightly && b.CreatedAt == start);
    }

    [Fact]
    public async Task SettingsFile_CarriesChosenSections_AndLibrariesToNewFolders()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var oldFolder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "Films")).FullName;
        var library = await client.PostAsJsonAsync(Api("/libraries"), new { name = "Films", type = "Movies", folders = new[] { oldFolder } });
        Assert.Equal(HttpStatusCode.Created, library.StatusCode);
        ServerConfigStore.Update(factory.DataDirectory, config =>
        {
            config["seerr"] = new JsonObject { ["baseUrl"] = "http://seerr.local:5055", ["apiKey"] = "seerr-key" };
            config["transcoding"] = new JsonObject { ["softwarePreset"] = "slow" };
        });

        var file = await (await client.PostAsJsonAsync(Api("/backups/settings-export"), new { })).Content.ReadAsByteArrayAsync();
        var exported = JsonNode.Parse(file)!;
        Assert.Null(exported["settings"]!["legacySignInsUntil"]);

        // The server is set up differently since: another library has the name, and the films have moved.
        await client.DeleteAsync(Api($"/libraries/{(await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}"));
        ServerConfigStore.Update(factory.DataDirectory, config =>
        {
            config.Remove("seerr");
            config["transcoding"] = new JsonObject { ["softwarePreset"] = "fast" };
        });
        var newFolder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "Moved films")).FullName;

        var upload = await UploadAsync(client, file);
        var settings = upload.GetProperty("settings");
        var import = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/import"), new
        {
            sections = new[] { "libraries", "connections" },
            libraries = new[] { new { name = "Films", folders = new[] { newFolder } } },
        });
        var result = await import.Content.ReadFromJsonAsync<JsonElement>();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(ServerConfigStore.PathFor(factory.DataDirectory)))!;
        var libraries = await client.GetFromJsonAsync<JsonElement>(Api("/libraries?all=true"));

        Assert.Contains("connections", settings.GetProperty("sections").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("streaming", settings.GetProperty("sections").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal(1, result.GetProperty("librariesAdded").GetInt32());
        Assert.Equal("seerr-key", config["seerr"]!["apiKey"]!.GetValue<string>());
        // Streaming wasn't chosen, so it's as it was.
        Assert.Equal("fast", config["transcoding"]!["softwarePreset"]!.GetValue<string>());
        Assert.Contains(libraries.EnumerateArray(), l => l.GetProperty("name").GetString() == "Films"
            && l.GetProperty("folders").EnumerateArray().Single().GetString() == newFolder);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == newFolder);
    }

    // ---------- Rogue files

    [Theory]
    [InlineData("{\"format\":5}")]
    [InlineData("{\"format\":\"flexarr-settings\",\"exportedAt\":42,\"flexarrVersion\":[1],\"settings\":[]}")]
    [InlineData("{\"format\":\"flexarr-settings\",\"format\":\"flexarr-settings\",\"settings\":{}}")]
    [InlineData("{\"format\":\"flexarr-settings\",\"settings\":{},\"libraries\":{\"name\":\"x\"}}")]
    [InlineData("{\"format\":\"flexarr-settings\",\"settings\":{},\"libraries\":[null,7,{\"name\":\"A\",\"type\":99,\"folders\":[\"/tmp\"]},{\"name\":\"B\",\"type\":\"Movies\",\"folders\":[\"/tmp\"]},{\"name\":\"b\",\"type\":\"Movies\",\"folders\":[\"/tmp\"]}]}")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[")]
    [InlineData("\"flexarr-settings\"")]
    [InlineData("")]
    public async Task MalformedSettingsFiles_AreRefusedOrCleaned_NeverAServerError(string json)
    {
        await using var factory = new FlexarrApiFactory();

        using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json));
        var response = await Client(factory).PostAsync(Api("/backups/uploads"), content);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var libraries = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("settings").GetProperty("libraries");
            // The library with a made-up type is dropped, and of two with one name only the first is offered.
            Assert.True(libraries.GetArrayLength() <= 1);
        }
    }

    [Fact]
    public async Task SettingsFile_CarriesOnlySettingsFlexarrKnows()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var file = """
            {"format":"flexarr-settings","settings":{
              "seerr":{"baseUrl":"http://seerr.local:5055","apiKey":"k"},
              "liveTv:password":"sneaky","dataDirectory":"/","legacySignInsUntil":"2099-01-01","madeUp":{"x":1},
              "ffmpegPath":"/tmp/not-ffmpeg"}}
            """u8.ToArray();

        var upload = await UploadAsync(client, file);
        var import = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/import"),
            new { sections = new[] { "connections", "other" } });
        var config = JsonNode.Parse(await File.ReadAllTextAsync(ServerConfigStore.PathFor(factory.DataDirectory)))!.AsObject();

        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal(["connections", "machine"], upload.GetProperty("settings").GetProperty("sections").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("k", config["seerr"]!["apiKey"]!.GetValue<string>());
        Assert.DoesNotContain(config, p => p.Key is "liveTv:password" or "dataDirectory" or "madeUp" or "ffmpegPath");
    }

    [Theory]
    [InlineData("{\"trustedProxies\":{\"a\":{\"b\":1}},\"transcoding\":{\"maxConcurrentTranscodes\":\"lots\"}}", "streaming")]
    [InlineData("{\"arr\":[{\"name\":\"Sonarr\",\"kind\":\"Sonarr\",\"baseUrl\":\"file:///etc/passwd\",\"apiKey\":\"k\"}]}", "connections")]
    [InlineData("{\"seerr\":{\"baseUrl\":\"gopher://x\",\"apiKey\":\"k\"}}", "connections")]
    public async Task SettingsTheServerCantUse_AreNotWritten(string settings, string section)
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var before = await File.ReadAllTextAsync(ServerConfigStore.PathFor(factory.DataDirectory));
        var file = System.Text.Encoding.UTF8.GetBytes($"{{\"format\":\"flexarr-settings\",\"settings\":{settings}}}");

        var upload = await UploadAsync(client, file);
        var import = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/import"), new { sections = new[] { section } });

        Assert.Equal(HttpStatusCode.BadRequest, import.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(ServerConfigStore.PathFor(factory.DataDirectory)));
        // The server still reads its settings.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api("/settings"))).StatusCode);
    }

    [Fact]
    public async Task ZipEntriesNamedToEscape_AreIgnored()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var backup = await NewBackupAsync(client);
        foreach (var name in new[] { "../../escaped.txt", "profile-images/../../escaped.jpg", "/escaped-abs.txt", "profile-images/..\\..\\escaped.jpg", "devices.json/../escaped" })
        {
            backup = WithEntry(backup, name, [1, 2, 3]);
        }

        var upload = await UploadAsync(client, backup);

        Assert.Equal(JsonValueKind.Null, upload.GetProperty("backup").GetProperty("problem").ValueKind);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(factory.DataDirectory)!, "escaped*", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(factory.DataDirectory, "escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ZipBombs_AreRefusedBeforeUnpacking()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var bomb = WithEntry(await NewBackupAsync(client), "flexarr.db", new byte[64 * 1024 * 1024]);

        using var content = new ByteArrayContent(bomb);
        var response = await client.PostAsync(Api("/backups/uploads"), content);

        Assert.True(bomb.Length < 1024 * 1024);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unpacks to far more", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(factory.DataDirectory, "backup-uploads"), "*", SearchOption.AllDirectories), f => new FileInfo(f).Length > 1024 * 1024);
    }

    [Theory]
    [InlineData("CREATE TRIGGER wipe AFTER INSERT ON PlaybackStates BEGIN DELETE FROM Profiles; END", "didn’t make")]
    [InlineData("CREATE VIEW snoop AS SELECT * FROM Profiles", "didn’t make")]
    [InlineData("DROP TABLE Collections", "doesn’t match")]
    [InlineData("ALTER TABLE Profiles DROP COLUMN Name", "doesn’t match")]
    public async Task DatabasesFlexarrDidntMake_AreNotRestored(string sql, string problem)
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var rogue = await WithDatabaseAsync(await NewBackupAsync(client), sql);

        var upload = await UploadAsync(client, rogue);
        var restore = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/restore"), new { });

        Assert.Contains(problem, upload.GetProperty("backup").GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal(0, factory.Restart.Requests);
        Assert.False(Directory.Exists(PendingRestore.FolderFor(factory.DataDirectory)));
    }

    [Fact]
    public async Task BackupWithSettingsTheServerCantRead_IsNotRestored()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var rogue = WithEntry(await NewBackupAsync(client), "config.json", "{\"transcoding\":{\"maxConcurrentTranscodes\":\"lots\"}}"u8.ToArray());

        var upload = await UploadAsync(client, rogue);

        Assert.Contains("settings in this backup", upload.GetProperty("backup").GetProperty("problem").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoredBackup_KeepsThisMachinesPaths_AndSaysWhenItsFromAnotherServer()
    {
        await using var elsewhere = new FlexarrApiFactory();
        await using var factory = new FlexarrApiFactory();
        ServerConfigStore.Update(elsewhere.DataDirectory, config => config["ffmpegPath"] = "/tmp/not-ffmpeg");
        var backup = await NewBackupAsync(Client(elsewhere));
        var client = Client(factory);

        var upload = await UploadAsync(client, backup);
        var own = await UploadAsync(client, await NewBackupAsync(client));
        await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/restore"), new { });
        var staged = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PendingRestore.FolderFor(factory.DataDirectory), "config.json")))!.AsObject();

        Assert.True(upload.GetProperty("fromAnotherServer").GetBoolean());
        Assert.False(own.GetProperty("fromAnotherServer").GetBoolean());
        Assert.False(staged.ContainsKey("ffmpegPath"));
        Assert.Equal(1, factory.Restart.Requests);
    }

    [Fact]
    public async Task ProtectedFileWithAHugeWorkFactor_IsRefusedQuickly()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream("{}"u8.ToArray()), encrypted, "correct horse", CancellationToken.None);
        var bytes = encrypted.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), int.MaxValue);

        var upload = await UploadAsync(client, bytes);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var open = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/open"), new { passphrase = "correct horse" });

        Assert.Equal(HttpStatusCode.BadRequest, open.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ChosenFolderThatIsntThere_IsNotMadeByABackup()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var share = Path.Combine(factory.DataDirectory, "share");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Api("/backups/settings"), new { folder = share })).StatusCode);
        Directory.Delete(share);

        var response = await client.PostAsync(Api("/backups"), null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(Directory.Exists(share));
    }

    [Fact]
    public async Task ADeviceWatchingAsAnotherProfile_CantTouchBackups()
    {
        await using var factory = new FlexarrApiFactory();
        var admin = Client(factory);
        await admin.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "4826" });
        var kid = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Kid" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Kid's TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = factory.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        await tv.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null });
        var name = (await (await admin.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api("/backups"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsync(Api("/backups"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api($"/backups/{name}/download"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsync(Api("/backups/uploads"), new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/backups/settings-export"), new { })).StatusCode);
    }

    [Fact]
    public void StagedRestore_FoundLongAfter_IsDropped()
    {
        var data = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(data, "flexarr.db"), "current");
            var unpacked = Directory.CreateDirectory(Path.Combine(data, "unpacked")).FullName;
            File.WriteAllText(Path.Combine(unpacked, "flexarr.db"), "old backup");
            var staged = DateTime.UtcNow.AddDays(-3);
            PendingRestore.Stage(data, unpacked, staged);

            Assert.False(PendingRestore.Apply(data, DateTime.UtcNow));
            Assert.Equal("current", File.ReadAllText(Path.Combine(data, "flexarr.db")));
            Assert.False(Directory.Exists(PendingRestore.FolderFor(data)));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void RestoreThatCantBePutInPlace_LeavesTheServerAsItWas()
    {
        var data = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(data, "flexarr.db"), "current");
            File.WriteAllText(Path.Combine(data, "flexarr.db-wal"), "current log");
            var unpacked = Directory.CreateDirectory(Path.Combine(data, "unpacked")).FullName;
            File.WriteAllText(Path.Combine(unpacked, "flexarr.db"), "backup");
            PendingRestore.Stage(data, unpacked, DateTime.UtcNow);
            // The current files can't be set aside: a file stands where their folder would go.
            File.WriteAllText(Path.Combine(data, "restore-previous"), "in the way");

            Assert.False(PendingRestore.Apply(data, DateTime.UtcNow));
            Assert.Equal("current", File.ReadAllText(Path.Combine(data, "flexarr.db")));
            Assert.Equal("current log", File.ReadAllText(Path.Combine(data, "flexarr.db-wal")));
            Assert.Single(Directory.EnumerateDirectories(data, "restore-failed-*"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task RestoredSettings_AreOnlyTheOnesFlexarrKnows()
    {
        await using var elsewhere = new FlexarrApiFactory();
        await using var factory = new FlexarrApiFactory();
        var backup = WithEntry(await NewBackupAsync(Client(elsewhere)), "config.json",
            """{"apiKey":"theirs","dataDirectory":"/tmp/elsewhere","backups:folder":"/tmp/planted","Transcoding":{"softwarePreset":"slow"}}"""u8.ToArray());
        var client = Client(factory);

        var upload = await UploadAsync(client, backup);
        var restore = await client.PostAsJsonAsync(Api($"/backups/uploads/{upload.GetProperty("id").GetString()}/restore"), new { });
        var staged = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PendingRestore.FolderFor(factory.DataDirectory), "config.json")))!.AsObject();

        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        Assert.Equal("theirs", staged["apiKey"]!.GetValue<string>());
        Assert.Equal("slow", staged["transcoding"]!["softwarePreset"]!.GetValue<string>());
        Assert.DoesNotContain(staged, p => p.Key is "dataDirectory" or "backups:folder");
    }

    [Fact]
    public async Task AccessSettings_NeedThePin_AndTrustingEveryoneIsRefused()
    {
        await using var factory = new FlexarrApiFactory();
        var admin = Client(factory);
        await admin.PostAsJsonAsync(Api("/admin/pin"), new { pin = "4826" });
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Laptop" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var browser = factory.CreateClient();
        browser.DefaultRequestHeaders.Add("X-Api-Key", token);
        var wide = """{"format":"flexarr-settings","settings":{"authenticationRequired":"DisabledForLocalAddresses","trustedProxies":["0.0.0.0/0"]}}"""u8.ToArray();
        var id = (await UploadAsync(browser, wide)).GetProperty("id").GetString();

        var withoutPin = await browser.PostAsJsonAsync(Api($"/backups/uploads/{id}/import"), new { sections = new[] { "access" }, confirm = true });
        var withPin = await browser.PostAsJsonAsync(Api($"/backups/uploads/{id}/import"), new { sections = new[] { "access" }, pin = "4826" });

        Assert.Equal(HttpStatusCode.Forbidden, withoutPin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withPin.StatusCode);
        Assert.Contains("too wide", await withPin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("trustedProxies", await File.ReadAllTextAsync(ServerConfigStore.PathFor(factory.DataDirectory)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackupsFolderThatCantBeUsed_IsSaidSo_NotAServerError()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        ServerConfigStore.Update(factory.DataDirectory, config => config["backups"] = new JsonObject { ["folder"] = "/nowhere/at\u0000all" });
        await Task.Delay(500);

        var list = await client.GetAsync(Api("/backups"));
        var create = await client.PostAsync(Api("/backups"), null);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("folderProblem").ValueKind);
        Assert.Equal(HttpStatusCode.InternalServerError, create.StatusCode);
        Assert.Contains("couldn’t be made", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZipsClaimingMillionsOfEntries_AreRefusedBeforeTheirDirectoryIsRead()
    {
        await using var factory = new FlexarrApiFactory();
        var client = Client(factory);
        var backup = await NewBackupAsync(client);
        // The end-of-central-directory record's entry counts, set to 60,000.
        var eocd = backup.Length - 22;
        Assert.Equal(0x06054b50u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(backup.AsSpan(eocd)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(backup.AsSpan(eocd + 8), 60_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(backup.AsSpan(eocd + 10), 60_000);

        using var content = new ByteArrayContent(backup);
        var response = await client.PostAsync(Api("/backups/uploads"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Encryption_RefusesBytesAfterTheEnd()
    {
        using var encrypted = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream([1, 2, 3]), encrypted, "correct horse", CancellationToken.None);
        encrypted.Write([0]);
        encrypted.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCrypto.DecryptAsync(encrypted, new MemoryStream(), "correct horse", CancellationToken.None));
    }

    /// <summary>The same backup, its database marked as from a migration this version doesn't have.</summary>
    private static Task<byte[]> WithMigrationAsync(byte[] backup, string migration) =>
        WithDatabaseAsync(backup, $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migration}', '10.0.0')");

    /// <summary>The same backup with one of its files replaced.</summary>
    private static byte[] WithEntry(byte[] backup, string name, byte[] contents)
    {
        using var stream = new MemoryStream();
        stream.Write(backup);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry(name)?.Delete();
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(contents);
        }

        return stream.ToArray();
    }

    private static async Task<byte[]> NewBackupAsync(HttpClient client)
    {
        var name = (await (await client.PostAsync(Api("/backups"), null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        return await client.GetByteArrayAsync(Api($"/backups/{name}/download"));
    }

    /// <summary>The same backup after running some SQL on its database.</summary>
    private static async Task<byte[]> WithDatabaseAsync(byte[] backup, string sql)
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-future-").FullName;
        try
        {
            var zipPath = Path.Combine(folder, "backup.zip");
            await File.WriteAllBytesAsync(zipPath, backup);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                var db = Path.Combine(folder, "flexarr.db");
                zip.GetEntry("flexarr.db")!.ExtractToFile(db);
                await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }

                zip.GetEntry("flexarr.db")!.Delete();
                zip.CreateEntryFromFile(db, "flexarr.db");
            }

            return await File.ReadAllBytesAsync(zipPath);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_restore_keeps_family_invites_from_this_server_or_else_the_backup()
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            var restored = Path.Combine(folder, "restored.json");
            var current = Path.Combine(folder, "current.json");
            File.WriteAllText(restored, """{"remoteGuests":{"enabled":true,"clientId":"from-backup"},"authenticationRequired":"Enabled"}""");
            File.WriteAllText(current, """{"remoteGuests":{"enabled":true,"clientId":"this-server"}}""");
            Flexarr.Api.Backups.SettingsTransfer.KeepMachinePaths(restored, current);
            Assert.Contains("this-server", File.ReadAllText(restored), StringComparison.Ordinal);

            File.WriteAllText(restored, """{"remoteGuests":{"enabled":true,"clientId":"from-backup"}}""");
            File.WriteAllText(current, "{}");
            Flexarr.Api.Backups.SettingsTransfer.KeepMachinePaths(restored, current);
            Assert.Contains("from-backup", File.ReadAllText(restored), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_restore_keeps_every_setting_but_this_machines_paths()
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            var restored = Path.Combine(folder, "restored.json");
            var current = Path.Combine(folder, "current.json");
            File.WriteAllText(restored, """
                {"apiKey":"theirs","serverId":"backup-server","setupOpen":false,"askPinForMainProfile":true,"ratingAges":"uk",
                 "pathMappingsDetected":["/tv"],"logs":{"errorReports":true,"retentionDays":7,"directory":"/elsewhere/logs"},
                 "updates":{"automatic":true},"bookMetadata":{"googleBooksKey":"g-key","hardcoverToken":"h-token","region":"uk"},
                 "setupGuide":{"dismissed":true},"transcoding":{"softwarePreset":"slow"},
                 "ffmpegPath":"/tmp/not-ffmpeg","cacheDirectory":"/elsewhere/cache","backups":{"folder":"/elsewhere/backups"},
                 "dataDirectory":"/","configRevision":"old","madeUp":1,"backups:folder":"/tmp/planted"}
                """);
            File.WriteAllText(current, """
                {"serverId":"this-server","ffmpegPath":"/usr/bin/ffmpeg","backups":{"folder":"/here/backups"},"logs":{"directory":"/here/logs"}}
                """);

            Flexarr.Api.Backups.SettingsTransfer.KeepMachinePaths(restored, current);
            var config = JsonNode.Parse(File.ReadAllText(restored))!.AsObject();

            Assert.Equal("backup-server", config["serverId"]!.GetValue<string>());
            Assert.Equal("theirs", config["apiKey"]!.GetValue<string>());
            Assert.False(config["setupOpen"]!.GetValue<bool>());
            Assert.True(config["askPinForMainProfile"]!.GetValue<bool>());
            Assert.Equal("uk", config["ratingAges"]!.GetValue<string>());
            Assert.Equal("/tv", config["pathMappingsDetected"]![0]!.GetValue<string>());
            Assert.True(config["logs"]!["errorReports"]!.GetValue<bool>());
            Assert.Equal(7, config["logs"]!["retentionDays"]!.GetValue<int>());
            Assert.Equal("/here/logs", config["logs"]!["directory"]!.GetValue<string>());
            Assert.True(config["updates"]!["automatic"]!.GetValue<bool>());
            Assert.Equal("g-key", config["bookMetadata"]!["googleBooksKey"]!.GetValue<string>());
            Assert.Equal("h-token", config["bookMetadata"]!["hardcoverToken"]!.GetValue<string>());
            Assert.Equal("uk", config["bookMetadata"]!["region"]!.GetValue<string>());
            Assert.True(config["setupGuide"]!["dismissed"]!.GetValue<bool>());
            Assert.Equal("slow", config["transcoding"]!["softwarePreset"]!.GetValue<string>());
            Assert.Equal("/usr/bin/ffmpeg", config["ffmpegPath"]!.GetValue<string>());
            Assert.Equal("/here/backups", config["backups"]!["folder"]!.GetValue<string>());
            Assert.DoesNotContain(config, p => p.Key is "cacheDirectory" or "dataDirectory" or "configRevision" or "madeUp" or "backups:folder");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_restore_of_a_backup_without_a_server_id_keeps_this_servers()
    {
        var folder = Directory.CreateTempSubdirectory("flexarr-restore-").FullName;
        try
        {
            var restored = Path.Combine(folder, "restored.json");
            var current = Path.Combine(folder, "current.json");
            File.WriteAllText(restored, """{"apiKey":"theirs","logs":{"directory":"/elsewhere/logs"}}""");
            File.WriteAllText(current, """{"serverId":"this-server"}""");

            Flexarr.Api.Backups.SettingsTransfer.KeepMachinePaths(restored, current);
            var config = JsonNode.Parse(File.ReadAllText(restored))!.AsObject();

            Assert.Equal("this-server", config["serverId"]!.GetValue<string>());
            Assert.False(config["logs"]!.AsObject().ContainsKey("directory"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
