using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class NfoParserTests
{
    [Fact]
    public void Movie_ReadsDetailsAndIds()
    {
        var nfo = NfoParser.Parse("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
            <movie>
              <title>Arrival</title>
              <originaltitle>Story of Your Life</originaltitle>
              <year>2016</year>
              <plot>A linguist works with the military.</plot>
              <mpaa>Rated PG-13</mpaa>
              <studio>Paramount</studio>
              <runtime>116</runtime>
              <premiered>2016-11-11</premiered>
              <genre>Drama</genre>
              <genre>Science Fiction / Mystery</genre>
              <ratings><rating name="imdb" max="10" default="true"><value>7.9</value></rating></ratings>
              <uniqueid type="tmdb">329865</uniqueid>
              <uniqueid type="imdb" default="true">tt2543164</uniqueid>
            </movie>
            """, "movie");

        Assert.NotNull(nfo);
        Assert.Equal("Arrival", nfo.Title);
        Assert.Equal("Story of Your Life", nfo.OriginalTitle);
        Assert.Equal(2016, nfo.Year);
        Assert.Equal("A linguist works with the military.", nfo.Plot);
        Assert.Equal("PG-13", nfo.ContentRating);
        Assert.Equal("Paramount", nfo.Studio);
        Assert.Equal(116, nfo.RuntimeMinutes);
        Assert.Equal(new DateOnly(2016, 11, 11), nfo.Premiered);
        Assert.Equal(["Drama", "Science Fiction", "Mystery"], nfo.Genres);
        Assert.Equal(7.9, nfo.Rating);
        Assert.Equal(new ExternalIds(null, "329865", "tt2543164"), nfo.Ids);
    }

    [Fact]
    public void Show_TakesTheOldIdAsTheTvdbOne()
    {
        var nfo = NfoParser.Parse("<tvshow><title>Severance</title><id>371980</id><mpaa>US:TV-MA</mpaa></tvshow>", "tvshow");

        Assert.Equal("371980", nfo?.Ids.TvdbId);
        Assert.Equal("TV-MA", nfo?.ContentRating);
    }

    [Fact]
    public void LinkAfterTheXml_FillsInMissingIds()
    {
        var nfo = NfoParser.Parse("<movie><title>Heat</title></movie>\nhttps://www.imdb.com/title/tt0113277/", "movie");

        Assert.Equal("Heat", nfo?.Title);
        Assert.Equal("tt0113277", nfo?.Ids.ImdbId);
    }

    [Theory]
    [InlineData("https://www.imdb.com/title/tt0113277/", null, "tt0113277")]
    [InlineData("https://www.themoviedb.org/movie/949-heat", "949", null)]
    public void LinkAlone_GivesJustTheId(string text, string? tmdb, string? imdb)
    {
        var nfo = NfoParser.Parse(text, "movie");

        Assert.NotNull(nfo);
        Assert.Null(nfo.Title);
        Assert.Equal(new ExternalIds(null, tmdb, imdb), nfo.Ids);
    }

    [Fact]
    public void RatingOutOfAHundred_IsOutOfTen()
    {
        var nfo = NfoParser.Parse("""<movie><ratings><rating name="tomatometer" max="100"><value>94</value></rating></ratings></movie>""", "movie");

        Assert.Equal(9.4, nfo?.Rating);
    }

    [Fact]
    public void YearComesFromThePremiereWhenMissing()
    {
        Assert.Equal(1995, NfoParser.Parse("<movie><premiered>1995-12-15</premiered></movie>", "movie")?.Year);
    }

    [Theory]
    [InlineData("<tvshow><title>Wrong kind</title></tvshow>")]
    [InlineData("<movie><title>Broken</movie>")]
    [InlineData("just some notes")]
    [InlineData("""<!DOCTYPE movie [<!ENTITY x "y">]><movie><title>&x;</title></movie>""")]
    public void NotAFilmNfo_IsNull(string text)
    {
        Assert.Null(NfoParser.Parse(text, "movie"));
    }
}
