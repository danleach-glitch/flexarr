using Flexarr.Core.Podcasts;

namespace Flexarr.Core.Tests;

public class PodcastChaptersTests
{
    [Fact]
    public void Notes_ListOfTimes_BecomesChapters()
    {
        var notes = "Romesh is joined by Jessica Biel and John Cena.\n\nChapters:\n\n00:00 Intro\n03:50 A gift for Jessica and John\n"
            + "05:12 Is London's food scene really that bad?\n52:50 Outro\n\nLearn more about your ad choices. Visit podcastchoices.com/adchoices";

        Assert.Equal(
            [new PodcastChapter(0, "Intro"), new PodcastChapter(230, "A gift for Jessica and John"),
             new PodcastChapter(312, "Is London's food scene really that bad?"), new PodcastChapter(3170, "Outro")],
            PodcastChapters.FromNotes(notes));
    }

    [Fact]
    public void Notes_HoursBracketsBulletsAndSeparators_AreRead()
    {
        var notes = "RUNNING ORDER:\n\n(00:00) The most goal-filled week ever\n\n• [05:58] - Man City 2-4 PSG\n\n28:44 | The Premier League weekend\n\n1:02:55: Outro";

        Assert.Equal(
            [new PodcastChapter(0, "The most goal-filled week ever"), new PodcastChapter(358, "Man City 2-4 PSG"),
             new PodcastChapter(1724, "The Premier League weekend"), new PodcastChapter(3775, "Outro")],
            PodcastChapters.FromNotes(notes));
    }

    [Fact]
    public void Notes_OneTimeOrTimesInSentences_AreNotChapters()
    {
        Assert.Empty(PodcastChapters.FromNotes("12:30 is when it all kicks off.\nWe recorded this on Tuesday."));
        Assert.Empty(PodcastChapters.FromNotes("At 12:30 we talk about football, and at 40:00 about cricket."));
        Assert.Empty(PodcastChapters.FromNotes(null));
    }

    [Fact]
    public void Notes_TimeGoingBackwards_EndsTheList_AndTimesPastTheEndAreDropped()
    {
        var notes = "00:00 Intro\n10:00 Part one\n20:00 Part two\n\nLast week:\n00:00 Old intro\n05:00 Old part";

        Assert.Equal(["Intro", "Part one", "Part two"], PodcastChapters.FromNotes(notes).Select(c => c.Title));
        Assert.Equal(["Intro", "Part one"], PodcastChapters.FromNotes(notes, durationSeconds: 900).Select(c => c.Title));
    }

    [Fact]
    public void Notes_BadTimes_AreSkipped()
    {
        Assert.Null(PodcastChapters.Timestamp("12:75"));
        Assert.Null(PodcastChapters.Timestamp("1:2:3"));
        Assert.Equal(3775, PodcastChapters.Timestamp("01:02:55"));
    }

    [Fact]
    public void Json_ReadsTheListedChapters_InOrder()
    {
        const string json = """
            {"version": "1.2.0", "chapters": [
                {"startTime": 120.5, "title": "The interview"},
                {"startTime": 0, "title": "Intro", "img": "https://example.com/a.jpg"},
                {"startTime": 60, "title": "Picture only", "toc": false},
                {"startTime": 90},
                {"startTime": "300", "title": "  Outro  "}
            ]}
            """;

        Assert.Equal(
            [new PodcastChapter(0, "Intro"), new PodcastChapter(120.5, "The interview"), new PodcastChapter(300, "Outro")],
            PodcastChapters.FromJson(json));
    }

    [Fact]
    public void Json_NotAChaptersFile_IsEmpty()
    {
        Assert.Empty(PodcastChapters.FromJson("<html>Not found</html>"));
        Assert.Empty(PodcastChapters.FromJson("""{"chapters": "none"}"""));
        Assert.Empty(PodcastChapters.FromJson("[]"));
    }
}
