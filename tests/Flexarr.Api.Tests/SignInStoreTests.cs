using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>devices.json: a damaged, unreadable or hand-edited file never stops browsers and TVs signing in.</summary>
public sealed class SignInStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-signins-").FullName;

    private string FilePath => Path.Combine(_dir, SignIns.FileName);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.GetFiles(_dir))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_dir, recursive: true);
    }

    private SignIns Store() => new(new FixedOptions(new FlexarrOptions { DataDirectory = _dir }), TimeProvider.System, NullLogger<SignIns>.Instance);

    private sealed class FixedOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;

        public FlexarrOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    [Fact]
    public void HandEditedLeftovers_AreSkipped_AndLongUnusedBrowsersPruned()
    {
        var recent = DateTime.UtcNow.AddDays(-1).ToString("O");
        var old = DateTime.UtcNow.AddDays(-400).ToString("O");
        File.WriteAllText(FilePath, $$"""
            [
              null,
              { "id": "a", "kind": "Device", "name": "TV", "tokenHash": "h1", "createdAt": "{{recent}}", "lastUsedAt": "{{recent}}" },
              { "id": "b", "kind": "Device", "name": "Copy", "tokenHash": "h1", "createdAt": "{{recent}}", "lastUsedAt": "{{recent}}" },
              { "id": "a", "kind": "Browser", "name": "Same id", "tokenHash": "h2", "createdAt": "{{recent}}", "lastUsedAt": "{{recent}}" },
              { "id": "c", "kind": "Browser", "name": "Forgotten", "tokenHash": "h3", "createdAt": "{{old}}", "lastUsedAt": "{{old}}" }
            ]
            """);

        var listed = Store().List();

        Assert.Equal("TV", Assert.Single(listed).Name);
        Assert.DoesNotContain("Forgotten", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceUnusedForAYear_IsSignedOut_ButOneUsedThisSummerIsnt()
    {
        var monthsAgo = DateTime.UtcNow.AddDays(-200).ToString("O");
        var overAYear = DateTime.UtcNow.AddDays(-370).ToString("O");
        File.WriteAllText(FilePath, $$"""
            [
              { "id": "h", "kind": "Device", "name": "Holiday home TV", "tokenHash": "h1", "createdAt": "{{monthsAgo}}", "lastUsedAt": "{{monthsAgo}}" },
              { "id": "s", "kind": "Device", "name": "Sold iPhone", "tokenHash": "h2", "createdAt": "{{overAYear}}", "lastUsedAt": "{{overAYear}}" }
            ]
            """);

        Assert.Equal("Holiday home TV", Assert.Single(Store().List()).Name);
    }

    [Fact]
    public void ABrowserSignedInWithThePin_LapsesSooner()
    {
        var weeksAgo = DateTime.UtcNow.AddDays(-20).ToString("O");
        File.WriteAllText(FilePath, $$"""
            [
              { "id": "k", "kind": "Browser", "name": "With the key", "tokenHash": "h1", "createdAt": "{{weeksAgo}}", "lastUsedAt": "{{weeksAgo}}" },
              { "id": "p", "kind": "Browser", "name": "With the PIN", "tokenHash": "h2", "createdAt": "{{weeksAgo}}", "lastUsedAt": "{{weeksAgo}}", "pinVerified": true }
            ]
            """);

        Assert.Equal("With the key", Assert.Single(Store().List()).Name);
    }

    [Fact]
    public void UnlinkingATailscaleLogin_SignsOutOnlyTheBrowsersItsPinSignInEarned()
    {
        var store = Store();
        var (_, earned) = store.Issue(SignInKind.Browser, "Safari on iPhone", NetworkZone.Tailnet, "100.70.1.30", pinVerified: true, tailscaleLogin: "owner@example.com");
        var (_, other) = store.Issue(SignInKind.Browser, "Chrome on Mac", NetworkZone.Tailnet, "100.70.1.31", pinVerified: true, tailscaleLogin: "partner@example.com");
        var (_, withKey) = store.Issue(SignInKind.Browser, "Firefox", NetworkZone.Local, "192.168.1.9");

        var revoked = store.RevokeEarnedBy("Owner@example.com");

        Assert.Equal(1, revoked);
        Assert.Null(store.Find(earned, SignInKind.Browser));
        Assert.NotNull(store.Find(other, SignInKind.Browser));
        Assert.NotNull(store.Find(withKey, SignInKind.Browser));
        Assert.Equal("partner@example.com", Assert.Single(Store().List(), s => s.Name == "Chrome on Mac").TailscaleLogin); // the login is saved
    }

    [Fact]
    public void AnUnreadableFile_IsKeptAside_AndSigningInStillWorks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(FilePath, "[]");
        File.SetUnixFileMode(FilePath, UnixFileMode.None); // as if a sudo run had made it root's
        var store = Store();

        var (_, token) = store.Issue(SignInKind.Device, "TV", NetworkZone.Local, "192.168.1.9");

        Assert.NotNull(store.Find(token, SignInKind.Device));
        Assert.Single(Directory.GetFiles(_dir, $"{SignIns.FileName}.unreadable-*"));
        Assert.NotNull(Store().Find(token, SignInKind.Device)); // and it was saved
    }

    [Fact]
    public void ADamagedFile_IsKeptAside_NotOverwritten()
    {
        File.WriteAllText(FilePath, "{ not json");

        Store().Issue(SignInKind.Browser, "Safari", NetworkZone.Local, null);

        var aside = Assert.Single(Directory.GetFiles(_dir, $"{SignIns.FileName}.unreadable-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }
}
