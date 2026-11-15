using System.Diagnostics;
using Flexarr.Core.Books;

namespace Flexarr.Core.Tests;

/// <summary>Files beside a book are anyone's: odd or huge ones are read quickly, or not at all.</summary>
public sealed class BookSidecarLimitTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("flexarr-sidecars-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_description_of_unclosed_tags_is_cleaned_in_a_moment()
    {
        var watch = Stopwatch.StartNew();
        var text = BookSidecars.PlainText(new string('<', 200_000) + "\n \n \n" + new string(' ', 100_000));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.StartsWith("<<<", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_and_paragraphs_still_become_plain_text()
    {
        Assert.Equal("One\n\nTwo\nThree & four", BookSidecars.PlainText("<p>One</p><div>Two<br/>Three &amp; <b>four</b></div>"));
    }

    [Fact]
    public void A_desc_txt_far_bigger_than_a_description_is_left_unread()
    {
        File.WriteAllText(Path.Combine(_folder, "desc.txt"), new string('a', 1024 * 1024));
        File.WriteAllText(Path.Combine(_folder, "reader.txt"), "Stephen Fry");

        var info = BookSidecars.Read(_folder);

        Assert.Null(info.Description);
        Assert.Equal(["Stephen Fry"], info.Narrators);
    }
}
