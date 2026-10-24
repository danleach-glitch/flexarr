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
