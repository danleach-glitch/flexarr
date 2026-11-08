using System.Text.Json;
using Flexarr.Core.Localization;

namespace Flexarr.Core.Tests.Localization;

/// <summary>The shared cases every platform's formatter runs (languages/tests/format-cases.json).</summary>
public sealed class MessageFormatTests
{
    private static readonly JsonElement Cases = Load();

    private static JsonElement Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "languages", "tests", "format-cases.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "languages", "tests", "format-cases.json"))).RootElement;
    }

    [Fact]
    public void Formats_every_shared_case()
    {
        foreach (var c in Cases.GetProperty("format").EnumerateArray())
        {
            var args = c.GetProperty("args").EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Number ? (object?)p.Value.GetDouble() : p.Value.GetString());
            var parts = MessageFormat.Parse(c.GetProperty("message").GetString()!);
            var got = MessageFormat.Format(parts, c.GetProperty("locale").GetString()!, n => args.TryGetValue(n, out var v) ? (true, v) : (false, null));
            Assert.Equal(c.GetProperty("expect").GetString(), got);
        }
    }

    [Fact]
    public void Refuses_every_shared_error()
    {
        foreach (var m in Cases.GetProperty("errors").EnumerateArray())
        {
            Assert.Throws<FormatException>(() => MessageFormat.Parse(m.GetString()!));
        }
    }

    [Theory]
    [InlineData("pl", 3, PluralCategory.Few)]
    [InlineData("pl", 25, PluralCategory.Many)]
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("de-AT", 1, PluralCategory.One)]
    [InlineData("zz", 1, PluralCategory.Other)]
    public void Picks_cldr_plural_forms(string code, double n, PluralCategory expected) =>
        Assert.Equal(expected, Plurals.Category(code, n));
}

public sealed class LocTests
{
    private static LanguagePack Pack(string code, long version, Dictionary<string, string> messages) =>
        new() { Code = code, Name = code, Version = version, Messages = messages };

    [Fact]
    public void Falls_back_to_the_English_string_by_string()
    {
        var catalog = new LanguageCatalog();
        catalog.Install(Pack("de", 1, new() { ["Play"] = "Abspielen", ["Broken {x}"] = "Kaputt {", ["Resume from {time}"] = "Fortsetzen ab {time}" }));
        var before = Loc.Catalog;
        Loc.Catalog = catalog;
        try
        {
            using (Loc.Use("de-AT"))
            {
                Assert.Equal("Abspielen", Loc.T("Play"));
                Assert.Equal("Fortsetzen ab 32:10", Loc.T("Resume from {time}", new { time = "32:10" }));
                Assert.Equal("Broken 1", Loc.T("Broken {x}", new { x = 1 }));
                Assert.Equal("Not translated", Loc.T("Not translated"));
            }

            Assert.Equal("Play", Loc.T("Play"));
            using (Loc.Use("de"))
            {
                Assert.Equal("Play", Loc.T("Play", ctx: "music"));
            }
        }
        finally
        {
            Loc.Catalog = before;
        }
    }

    [Fact]
    public void A_newer_pack_replaces_an_older_one_only()
    {
        var catalog = new LanguageCatalog();
        Assert.True(catalog.Install(Pack("de", 2, new() { ["Play"] = "B" })));
        Assert.False(catalog.Install(Pack("de", 1, new() { ["Play"] = "A" })));
        Assert.Equal("B", catalog.Find("de")!.Messages["Play"]);
    }

    [Fact]
    public void English_strips_a_context() =>
        Assert.Equal(("Play", "Play", "Fix:: this"), (Loc.English("music::Play"), Loc.English("Play"), Loc.English("Fix:: this")));

    [Fact]
    public void Refuses_values_nested_deeper_than_eight_without_overflowing()
    {
        static string Nest(int levels) => string.Concat(Enumerable.Repeat("{a, select, other {", levels)) + "x" + string.Concat(Enumerable.Repeat("}}", levels));

        Assert.NotEmpty(MessageFormat.Parse(Nest(MessageFormat.MaxDepth)));
        Assert.Throws<FormatException>(() => MessageFormat.Parse(Nest(MessageFormat.MaxDepth + 1)));
        // Run on a small stack, as a deeply nested pack would overflow any: without the limit this ends the process.
        Exception? thrown = null;
        var thread = new Thread(() => thrown = Record.Exception(() => MessageFormat.Parse(Nest(100_000))), 256 * 1024);
        thread.Start();
        thread.Join();
        Assert.IsType<FormatException>(thrown);
        Assert.False(LanguagePack.Usable("Play", Nest(100_000)));
    }

    [Fact]
    public void Reading_a_pack_drops_messages_whose_values_differ()
    {
        var json = """{"format":1,"code":"de","name":"Deutsch","direction":"ltr","version":1,"messages":{"Hi {name}":"Hallo {nom}","Play":"Abspielen"},"scopes":{}}""";
        var pack = LanguagePack.Read(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(["Play"], pack.Messages.Keys);
    }

    [Fact]
    public void Reading_refuses_a_bad_code()
    {
        var json = """{"format":1,"code":"../x","name":"x","direction":"ltr","version":1,"messages":{}}""";
        Assert.Throws<InvalidDataException>(() => LanguagePack.Read(System.Text.Encoding.UTF8.GetBytes(json)));
    }
}
