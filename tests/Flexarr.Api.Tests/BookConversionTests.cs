using System.Net;
using System.Net.Http.Json;
using Flexarr.Api.Books;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Converting book files a player can't decode: a job of the server's, not of the request that asked.</summary>
public sealed class BookConversionTests : IDisposable
{
    private readonly FlexarrApiFactory _server = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _running;
    private int _mostAtOnce;

    public BookConversionTests()
    {
        var converter = _server.Services.GetRequiredService<BookFileConverter>();
        converter.Encode = async (_, output, ct) =>
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _running);
            lock (_gate)
            {
                _mostAtOnce = Math.Max(_mostAtOnce, now);
            }

            try
            {
                await _gate.Task.WaitAsync(ct);
                await File.WriteAllTextAsync(output, "converted", ct);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        };
    }

    private BookFileConverter Converter => _server.Services.GetRequiredService<BookFileConverter>();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task AConversion_CarriesOn_WhenTheRequestGivesUp()
    {
        var book = await SeedAsync(("01.wma", "wmav2"));

        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().GetAsync(Url($"/api/v1/items/{book}/book/files/0?convert=aac"), impatient.Token));
        }

        _gate.SetResult();
        var retry = await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/0?convert=aac"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("converted", await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, _started); // not started again from nothing
        Assert.Empty(Directory.EnumerateFiles(Converter.Directory, "*.part"));
    }

    [Fact]
    public async Task HandingOutTheManifest_StartsConverting()
    {
        var book = await SeedAsync(("01.mp3", "mp3"), ("02.wma", "wmav2"));

        (await Client().GetAsync(Url($"/api/v1/items/{book}/book?codecs=aac,mp3"))).EnsureSuccessStatusCode();

        await WaitForAsync(() => _started == 1);
        _gate.SetResult();
        var file = await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/1?convert=aac"));
        Assert.Equal("converted", await file.Content.ReadAsStringAsync());
        Assert.Equal(1, _started);
    }

    [Fact]
    public async Task OnlyAFewConversionsRunAtOnce()
    {
        var book = await SeedAsync(("01.wma", "wmav2"), ("02.wma", "wmav2"), ("03.wma", "wmav2"));

        (await Client().GetAsync(Url($"/api/v1/items/{book}/book?codecs=aac,mp3"))).EnsureSuccessStatusCode();
        await WaitForAsync(() => _started == BookFileConverter.MaxConcurrent);
        await Task.Delay(300);
        Assert.Equal(BookFileConverter.MaxConcurrent, _started);

        _gate.SetResult();
        await WaitForAsync(() => _started == 3 && _running == 0);
        Assert.Equal(BookFileConverter.MaxConcurrent, _mostAtOnce);
    }

    [Theory]
    [InlineData("none")]
    [InlineData(",")]
    public async Task APlayerClaimingNoCodecs_StillPlaysAacAndMp3AsTheyAre(string codecs)
    {
        var book = await SeedAsync(("01.mp3", "mp3"), ("02.m4b", "aac"));
        _gate.SetResult();

        var manifest = await Client().GetFromJsonAsync<BookManifestDto>(Url($"/api/v1/items/{book}/book?codecs={codecs}"));

        Assert.All(manifest!.Files, f => Assert.False(f.Converted));
        await Task.Delay(300);
        Assert.Equal(0, _started);
    }

    [Fact]
    public async Task OneRequest_StartsOnlyAFewConversions_AndTheRestWaitToBeAskedFor()
    {
        var book = await SeedAsync([.. Enumerable.Range(1, 8).Select(i => ($"{i:00}.wma", "wmav2"))]);
        _gate.SetResult();

        (await Client().GetAsync(Url($"/api/v1/items/{book}/book?codecs=aac,mp3"))).EnsureSuccessStatusCode();
        await WaitForAsync(() => _started == BookFileConverter.MaxPrepared && _running == 0);
        await Task.Delay(300);
        Assert.Equal(BookFileConverter.MaxPrepared, _started);

        var last = await Client().GetAsync(Url($"/api/v1/items/{book}/book/files/7?convert=aac"));
        Assert.Equal("converted", await last.Content.ReadAsStringAsync());
        Assert.Equal(BookFileConverter.MaxPrepared + 1, _started);
    }

    [Fact]
    public async Task OldCopiesArePruned_AndABooksCopiesGoWithIt()
    {
        var directory = Directory.CreateDirectory(Converter.Directory).FullName;
        var old = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a");
        var recent = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a");
        var abandoned = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a.x.part");
        var deleted = Guid.NewGuid();
        var ofDeletedBook = Path.Combine(directory, $"{deleted:N}-1-1.m4a");
        foreach (var file in new[] { old, recent, abandoned, ofDeletedBook })
        {
            await File.WriteAllTextAsync(file, "x");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - BookFileConverter.UnusedFor - TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));

        Converter.Prune();
        Converter.RemoveCopies([deleted]);

        Assert.Equal([recent], Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task OverTheCap_TheLeastRecentlyPlayedGoFirst()
    {
        var directory = Directory.CreateDirectory(Converter.Directory).FullName;
        var older = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a");
        var newer = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a");
        var playing = Path.Combine(directory, $"{Guid.NewGuid():N}-1-1.m4a");
        foreach (var file in new[] { older, newer, playing })
        {
            await File.WriteAllTextAsync(file, "0123456789");
        }

        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-2));

        Converter.Prune(maxBytes: 20);

        Assert.Equal(new[] { newer, playing }.Order(), Directory.EnumerateFiles(directory).Order());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private async Task<Guid> SeedAsync(params (string Name, string Codec)[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_server.MediaDirectory, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, file.Name), file.Name);
        }

        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "A Book", SortTitle = "book", Path = folder,
            Book = new BookDetails { DurationSeconds = files.Length * 600 },
            Files = [.. files.Select((f, i) => new MediaFile { Path = Path.Combine(folder, f.Name), AudioCodec = f.Codec, DurationSeconds = 600, BookOrder = i, Size = 10 })],
        };
        db.AddRange(library, book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }
}
