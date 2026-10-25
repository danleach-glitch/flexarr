using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Probing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>GET /items/{id}/loudness: a track's ReplayGain from its tags, probed once and kept on disk.</summary>
public class LoudnessTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ATaggedFlac_GivesItsGainsAndPeaks_ProbedOnce()
    {
        var probe = new FixtureProbe();
        using var host = WithProbe(probe);
        var (track, path) = await SeedTrackAsync(host, "02 - News.flac");
        await File.WriteAllBytesAsync(path, [0x66, 0x4C, 0x61, 0x43]); // "fLaC": the probe is faked, but the file has to be there.

        var loudness = await Client(host).GetFromJsonAsync<LoudnessDto>(new Uri($"/api/v1/items/{track}/loudness", UriKind.Relative), Json);

        Assert.Equal(new LoudnessDto(-7.52, -8.10, 0.988525, 1.0), loudness);
        Assert.Equal(1, probe.Calls);

        // The second request is answered from cache/loudness, not by ffprobe.
        Assert.Equal(loudness, await Client(host).GetFromJsonAsync<LoudnessDto>(new Uri($"/api/v1/items/{track}/loudness", UriKind.Relative), Json));
        Assert.Equal(1, probe.Calls);
        Assert.Contains(Directory.GetFiles(Path.Combine(factory.DataDirectory, "cache", "loudness")), f => f.EndsWith(".json", StringComparison.Ordinal));

        // A retagged file (its modification time moved on) is probed again.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        await Client(host).GetAsync(new Uri($"/api/v1/items/{track}/loudness", UriKind.Relative));
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task AFileWithoutTags_OrOneThatIsntThere_PlaysAtItsOwnLevel()
    {
        var probe = new FixtureProbe { Tags = new Dictionary<string, string>() };
        using var host = WithProbe(probe);
        var (tagless, path) = await SeedTrackAsync(host, "03 - Where Do You Think You're Going.flac");
        await File.WriteAllBytesAsync(path, [0x66, 0x4C, 0x61, 0x43]);
        var (missing, _) = await SeedTrackAsync(host, "04 - Angel of Mercy.flac");

        Assert.Equal(new LoudnessDto(null, null, null, null), await Client(host).GetFromJsonAsync<LoudnessDto>(new Uri($"/api/v1/items/{tagless}/loudness", UriKind.Relative), Json));
        Assert.Equal(new LoudnessDto(null, null, null, null), await Client(host).GetFromJsonAsync<LoudnessDto>(new Uri($"/api/v1/items/{missing}/loudness", UriKind.Relative), Json));
        Assert.Equal(1, probe.Calls); // Only the file that exists was worth probing.
    }

    [Fact]
    public async Task NotATrack_Is404()
    {
        using var host = WithProbe(new FixtureProbe());
        var (track, _) = await SeedTrackAsync(host, "01 - Once Upon a Time in the West.flac");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var album = db.MediaItems.First(i => i.Id == db.MediaItems.First(t => t.Id == track).ParentId);

        Assert.Equal(HttpStatusCode.NotFound, (await Client(host).GetAsync(new Uri($"/api/v1/items/{album.Id}/loudness", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(host).GetAsync(new Uri($"/api/v1/items/{Guid.NewGuid()}/loudness", UriKind.Relative))).StatusCode);
    }

    private WebApplicationFactory<Program> WithProbe(IMediaProbe probe) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(probe)));

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>One track on one album, whose file is a path in a scratch folder.</summary>
    private async Task<(Guid Track, string Path)> SeedTrackAsync(WebApplicationFactory<Program> host, string fileName)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"music-{Guid.NewGuid():N}", "Dire Straits", "Communiqué (1979)")).FullName;
        var path = Path.Combine(folder, fileName);

        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Dire Straits", SortTitle = "dire straits" };
        var album = new MediaItem { LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = "Communiqué", SortTitle = "communiqué", Year = 1979 };
        var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = Path.GetFileNameWithoutExtension(fileName), SortTitle = "track" };
        db.AddRange(library, artist, album, track, new MediaFile { MediaItemId = track.Id, Path = path, Container = "flac", AudioCodec = "flac" });
        await db.SaveChangesAsync();
        return (track.Id, path);
    }

    /// <summary>Stands in for ffprobe: answers with the tags ffprobe printed for a real tagged FLAC (Fixtures/ffprobe-flac-replaygain.json), or with none.</summary>
    private sealed class FixtureProbe : IMediaProbe
    {
        public int Calls { get; private set; }

        public IReadOnlyDictionary<string, string>? Tags { get; init; }

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "ffprobe-flac-replaygain.json")));
            var tags = json.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
                .ToDictionary(t => t.Name, t => t.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ProbeResult("flac", 0.2, 465080, VideoRange.Sdr, [new MediaStream { Index = 0, Type = StreamType.Audio, Codec = "flac", Channels = 1 }]) { Tags = Tags ?? tags });
        }
    }
}
