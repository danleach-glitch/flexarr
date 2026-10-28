using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Flexarr.Api.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public sealed class RuntimeInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flexarr-dotnet-").FullName;

    private string DotnetRoot => Path.Combine(_root, "dotnet");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Latest_ReadsTheLatestPatchForThisMac_AndRefusesAnythingOdd()
    {
        var patch = RuntimeInstaller.Latest(Metadata("10.0.14", "10.0.13", Hash('a')), "osx-arm64");

        Assert.NotNull(patch);
        Assert.Equal("10.0.14", patch.AspNetCore);
        Assert.Equal("10.0.13", patch.NetCore);
        Assert.Equal(new string('a', 128), patch.Sha512);
        Assert.Null(RuntimeInstaller.Latest(Metadata("10.0.14", "10.0.13", Hash('a'), url: "https://evil.example/runtime.tar.gz"), "osx-arm64"));
        Assert.Null(RuntimeInstaller.Latest(Metadata("10.0.14;rm", "10.0.13", Hash('a')), "osx-arm64"));
        Assert.Null(RuntimeInstaller.Latest(Metadata("10.0.14", "10.0.13", "short"), "osx-arm64"));
        Assert.Null(RuntimeInstaller.Latest(Metadata("10.0.14", "10.0.13", Hash('a')), "linux-x64"));
    }

    [Fact]
    public void ChannelFor_IsTheAspNetCoreVersionTheAppNeeds()
    {
        var app = App("10.0.0");

        Assert.Equal("10.0", RuntimeInstaller.ChannelFor(app));
    }

    [Fact]
    public async Task EnsureLatest_InstallsAMissingPatch_KeepingTheDotnetHostThatsThere()
    {
        Directory.CreateDirectory(DotnetRoot);
        await File.WriteAllTextAsync(Path.Combine(DotnetRoot, "dotnet"), "the host macOS granted access to");
        // An earlier patch, untouched, and a half-installed copy of the new one, replaced.
        Directory.CreateDirectory(Path.Combine(DotnetRoot, "shared", "Microsoft.AspNetCore.App", "10.0.3"));
        Directory.CreateDirectory(Path.Combine(DotnetRoot, "shared", "Microsoft.NETCore.App", "10.0.13"));
        var archive = Archive("10.0.14", "10.0.13");
        var installer = Installer(Metadata("10.0.14", "10.0.13", Sha512(archive)), archive);
        var seen = 0L;

        var installed = await installer.EnsureLatestAsync(App("10.0.0"), DotnetRoot, "osx-arm64", (received, _) => seen = received, CancellationToken.None);
        var again = await installer.EnsureLatestAsync(App("10.0.0"), DotnetRoot, "osx-arm64", (_, _) => { }, CancellationToken.None);

        Assert.Equal("10.0.14", installed);
        Assert.Null(again);
        Assert.Equal(archive.Length, seen);
        Assert.True(RuntimeInstaller.IsInstalled(DotnetRoot, new RuntimePatch("10.0.14", "10.0.13", new Uri("https://builds.dotnet.microsoft.com/x"), "")));
        Assert.True(Directory.Exists(Path.Combine(DotnetRoot, "shared", "Microsoft.AspNetCore.App", "10.0.3")));
        Assert.Equal("the host macOS granted access to", await File.ReadAllTextAsync(Path.Combine(DotnetRoot, "dotnet")));
        Assert.Empty(Directory.EnumerateDirectories(DotnetRoot, ".staging-*"));
    }

    [Fact]
    public async Task EnsureLatest_ADownloadThatDoesNotMatchItsChecksum_InstallsNothing()
    {
        var archive = Archive("10.0.14", "10.0.13");
        var installer = Installer(Metadata("10.0.14", "10.0.13", Hash('b')), archive);

        await Assert.ThrowsAsync<UpdateException>(() => installer.EnsureLatestAsync(App("10.0.0"), DotnetRoot, "osx-arm64", (_, _) => { }, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(DotnetRoot, "shared")));
        Assert.Empty(Directory.EnumerateDirectories(DotnetRoot, ".staging-*"));
    }

    private string App(string aspNetCore)
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "Flexarr.runtimeconfig.json"), $$$"""
            {"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"{{{aspNetCore}}}"}]}}
            """);
        return app;
    }

    private static RuntimeInstaller Installer(string metadata, byte[] archive)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(UpdateChecker.DownloadClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeMicrosoft(metadata, archive));
        return new RuntimeInstaller(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), NullLogger<RuntimeInstaller>.Instance);
    }

    private static string Hash(char digit) => new(digit, 128);

    private static string Sha512(byte[] data) => Convert.ToHexStringLower(SHA512.HashData(data));

    private static string Metadata(string aspNetCore, string netCore, string hash, string url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/runtime.tar.gz") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["latest-release"] = "10.0.14",
            ["releases"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["release-version"] = "10.0.14",
                    ["runtime"] = new Dictionary<string, object> { ["version"] = netCore },
                    ["aspnetcore-runtime"] = new Dictionary<string, object>
                    {
                        ["version"] = aspNetCore,
                        ["files"] = new[] { new Dictionary<string, object> { ["name"] = "aspnetcore-runtime-osx-arm64.tar.gz", ["url"] = url, ["hash"] = hash } },
                    },
                },
            },
        });

    /// <summary>The layout of Microsoft's aspnetcore-runtime archive, with stand-in files.</summary>
    private static byte[] Archive(string aspNetCore, string netCore)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var path in new[]
            {
                "dotnet", "LICENSE.txt",
                $"host/fxr/{netCore}/libhostfxr.dylib",
                $"shared/Microsoft.NETCore.App/{netCore}/Microsoft.NETCore.App.deps.json",
                $"shared/Microsoft.NETCore.App/{netCore}/libcoreclr.dylib",
                $"shared/Microsoft.AspNetCore.App/{aspNetCore}/Microsoft.AspNetCore.App.deps.json",
            })
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + path) { DataStream = new MemoryStream("x"u8.ToArray()) });
            }
        }

        return output.ToArray();
    }

    private sealed class FakeMicrosoft(string metadata, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/10.0/releases.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
    }
}
