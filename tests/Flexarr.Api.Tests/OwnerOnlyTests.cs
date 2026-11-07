using Flexarr.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>The database, logs and cache are kept from the machine's other accounts, as config.json is.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // each test returns at once there: Windows has no such permissions
public sealed class OwnerOnlyTests : IDisposable
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode OpenFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode OpenFolder = OpenFile | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-owner-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder(params string[] parts)
    {
        var path = Directory.CreateDirectory(Path.Combine([_dir, .. parts])).FullName;
        File.SetUnixFileMode(path, OpenFolder);
        return path;
    }

    private static string FileIn(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, OpenFile);
        return path;
    }

    [Fact]
    public void TheDatabaseLogsAndCache_AreTakenFromOtherAccounts_AndTheDataFolderIsLeftAsItIs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Folder("data");
        var files = new[] { "flexarr.db", "flexarr.db-wal", "flexarr.db-shm" }.Select(name => FileIn(data, name)).ToList();
        var logs = Folder("data", "logs");
        var cache = Path.Combine(data, "cache"); // not made yet

        OwnerOnly.Apply(data, logs, cache, null);

        Assert.All(files, file => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file)));
        Assert.All([logs, cache], folder => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder)));
        // Docker's /config, say: the person's own folder, with whatever else they keep in it.
        Assert.Equal(OpenFolder, File.GetUnixFileMode(data));
    }

    [Fact]
    public void LogAndCacheFoldersSetElsewhere_AreLeftAlone_AndAMacInstallsFolderIsClosedWhole()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Folder("install");
        var data = Folder("install", "data");
        var logs = Folder("install", "logs"); // beside the data folder, as the Mac install has it
        var scratch = Folder("scratch"); // a drive other things use too
        var database = FileIn(data, "flexarr.db");

        OwnerOnly.Apply(data, logs, scratch, null);
        Assert.Equal(OpenFolder, File.GetUnixFileMode(scratch));
        Assert.Equal(OpenFolder, File.GetUnixFileMode(logs));
        Assert.Equal(OpenFolder, File.GetUnixFileMode(root));

        OwnerOnly.Apply(data, logs, scratch, root);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
        Assert.Equal(OpenFolder, File.GetUnixFileMode(scratch));
    }

    [Fact]
    public void WhatIsntThere_OrIsReadOnly_IsNoTrouble()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Folder("data");
        var database = FileIn(data, "flexarr.db");
        File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        OwnerOnly.Apply(data, Path.Combine(data, "logs"), Path.Combine(_dir, "nowhere", "cache"), Path.Combine(_dir, "no-such-install"));

        // The owner's own access stays as it was.
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(database));
        Assert.False(Directory.Exists(Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void ACacheFolderSetToNothing_OrToTheDataFolderItself_DoesntStopTheServerOrCloseTheDataFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Folder("data");
        var database = FileIn(data, "flexarr.db");

        // "CacheDirectory": "" in the settings is a cache in the folder the server runs from: nothing of the data folder's.
        OwnerOnly.Apply(data, Path.Combine(data, "logs"), "", null);
        OwnerOnly.Apply(data, Path.Combine(data, "logs"), " ", null);
        // And one set to the data folder, however it's written, isn't a folder inside it.
        OwnerOnly.Apply(data, Path.Combine(data, "logs"), data + Path.DirectorySeparatorChar, null);
        OwnerOnly.Apply(data + Path.DirectorySeparatorChar, Path.Combine(data, "logs"), Path.Combine(data, "cache", "..") + Path.DirectorySeparatorChar, null);

        Assert.Equal(OpenFolder, File.GetUnixFileMode(data));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(data, "logs")));
    }

    [Fact]
    public void AStartedServer_KeepsItsDatabaseAndLogsToItsOwnAccount()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var factory = new FlexarrApiFactory();
        _ = factory.Services.GetRequiredService<Flexarr.Api.Logging.LogPaths>(); // starts the server

        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(factory.DataDirectory, "flexarr.db")) & Others);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(factory.DataDirectory, "logs")) & Others);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(factory.DataDirectory, "cache")) & Others);
    }
}
