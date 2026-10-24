using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flexarr.Api.Tests;

/// <summary>The version is written in four places; a release that bumps some of them and not the others fails here.</summary>
public partial class VersionTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Flexarr.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Flexarr.slnx not found above the test output");
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [Fact]
    public void EveryPlaceTheVersionIsWritten_AgreesWithTheChangelog()
    {
        var server = VersionPrefix().Match(Read("Directory.Build.props")).Groups[1].Value;
        var web = JsonDocument.Parse(Read("frontend/package.json")).RootElement.GetProperty("version").GetString();
        var apple = MarketingVersion().Match(Read("clients/apple/project.yml")).Groups[1].Value;
        // The newest numbered heading, skipping "## Unreleased".
        var changelog = ChangelogVersion().Match(Read("CHANGELOG.md")).Groups[1].Value;

        Assert.Matches(@"^\d+\.\d+\.\d+$", server);
        Assert.Equal(server, web);
        Assert.Equal(server, apple);
        Assert.Equal(server, changelog);
    }

    [GeneratedRegex("<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex("MARKETING_VERSION: \"([^\"]+)\"")]
    private static partial Regex MarketingVersion();

    [GeneratedRegex(@"(?m)^## (\d+\.\d+\.\d+) · ")]
    private static partial Regex ChangelogVersion();
}
