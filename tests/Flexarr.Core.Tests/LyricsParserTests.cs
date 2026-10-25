using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class LyricsParserTests
{
    [Fact]
    public void Lrc_TimesEveryLine_AndDropsTheTagLines()
    {
        var lyrics = LyricsParser.Parse("[ar:Dire Straits]\n[ti:News]\n[al:Communiqué]\n[00:12.50]He sticks to his guns\r\n[00:16.000]He takes the road as it comes\n[01:02]It takes the shine off his shoes\n");

        Assert.NotNull(lyrics);
        Assert.True(lyrics.Synced);
        Assert.Equal(
            [new LyricLine(12.5, "He sticks to his guns"), new LyricLine(16, "He takes the road as it comes"), new LyricLine(62, "It takes the shine off his shoes")],
            lyrics.Lines);
    }

    [Fact]
    public void Lrc_ALineWithSeveralTimes_IsSungEachTime_InOrder()
    {
        var lyrics = LyricsParser.Parse("[00:30.00][01:30.00]Chorus\n[00:10.00]Verse one\n[01:10.00]Verse two");

        Assert.NotNull(lyrics);
        Assert.Equal(["Verse one", "Chorus", "Verse two", "Chorus"], lyrics.Lines.Select(l => l.Text));
        Assert.Equal([10.0, 30.0, 70.0, 90.0], lyrics.Lines.Select(l => l.Time));
    }

    [Fact]
    public void Lrc_Offset_MovesEveryLineEarlier_ButNotBeforeTheStart()
    {
        var lyrics = LyricsParser.Parse("[offset:+500]\n[00:00.20]Intro\n[00:10.00]Verse");

        Assert.NotNull(lyrics);
        Assert.Equal([0.0, 9.5], lyrics.Lines.Select(l => l.Time));

        var later = LyricsParser.Parse("[offset:-250]\n[00:10.00]Verse");
        Assert.Equal(10.25, later!.Lines[0].Time);
    }

    [Fact]
    public void Lrc_WordTimes_AreStripped_AndUntimedLinesLeftOut()
    {
        var lyrics = LyricsParser.Parse("Lyrics by someone\n[00:05.00]<00:05.00>Once <00:05.40>upon <00:05.90>a time\n[00:08:50]In the west\n[00:12.00]\n");

        Assert.NotNull(lyrics);
        Assert.Equal([new LyricLine(5, "Once upon a time"), new LyricLine(8.5, "In the west"), new LyricLine(12, "")], lyrics.Lines);
    }

    [Fact]
    public void PlainText_KeepsVersesApart_WithOneBlankLine()
    {
        var lyrics = LyricsParser.Parse("﻿\n\nFirst verse, line one\nFirst verse, line two\n\n\n\nSecond verse\n\n");

        Assert.NotNull(lyrics);
        Assert.False(lyrics.Synced);
        Assert.Equal(["First verse, line one", "First verse, line two", "", "Second verse"], lyrics.Lines.Select(l => l.Text));
        Assert.All(lyrics.Lines, l => Assert.Null(l.Time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\n\r\n")]
    [InlineData("[ar:Someone]\n[ti:A title]")]
    [InlineData("[00:01.00]\n[00:02.00]")]
    public void NothingToShow_IsNull(string text) => Assert.Null(LyricsParser.Parse(text));

    [Fact]
    public void FromTags_FindsLyricsUnderTheNamesTaggersUse_PreferringSynced()
    {
        var vorbis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["LYRICS"] = "Plain words", ["TITLE"] = "News" };
        Assert.Equal("Plain words", LyricsParser.FromTags(vorbis)!.Lines[0].Text);

        var id3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["lyrics-eng"] = "From USLT" };
        Assert.Equal("From USLT", LyricsParser.FromTags(id3)!.Lines[0].Text);

        var both = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UNSYNCEDLYRICS"] = "Plain words", ["SYLT"] = "[00:01.00]Timed words" };
        var picked = LyricsParser.FromTags(both);
        Assert.True(picked!.Synced);
        Assert.Equal("Timed words", picked.Lines[0].Text);

        Assert.Null(LyricsParser.FromTags(new Dictionary<string, string> { ["title"] = "News", ["comment"] = "Not lyrics" }));
    }
}
