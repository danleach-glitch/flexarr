using Flexarr.Core.Media;
using Flexarr.Streaming.Markers;

namespace Flexarr.Core.Tests;

public class MarkerDetectorTests
{
    [Fact]
    public void FromChapters_RecognisesIntroRecapAndCredits()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0.000000","end_time":"45.000000","tags":{"title":"Previously On"}},
              {"start_time":"45.000000","end_time":"105.500000","tags":{"title":"Opening"}},
              {"start_time":"105.500000","end_time":"2500.000000","tags":{"title":"Chapter 2"}},
              {"start_time":"2500.000000","end_time":"2640.000000","tags":{"title":"End Credits"}}
            ]}
            """;

        var markers = MarkerDetector.FromChapters(json);

        Assert.Equal(
            [(MarkerType.Recap, 0d, 45d), (MarkerType.Intro, 45d, 105.5d), (MarkerType.Credits, 2500d, 2640d)],
            markers.Select(m => (m.Type, m.Start, m.End)));
    }

    /// <summary>Peacemaker S2E6: a chapter called "Credits" at 199–300 s of a 36-minute episode is the opening titles.</summary>
    [Fact]
    public void FromChapters_TakesEarlyCreditsAsTheOpeningTitles_AnIntro()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0","end_time":"198.907","tags":{"title":"Chapter 1"}},
              {"start_time":"198.907","end_time":"299.758","tags":{"title":"Credits"}},
              {"start_time":"299.758","end_time":"2154.304","tags":{"title":"Chapter 2"}}]}
            """;

        var marker = Assert.Single(MarkerDetector.FromChapters(json));

        Assert.Equal(MarkerType.Intro, marker.Type);
        Assert.Equal(198.907, marker.Start, 3);
        // At the end, credits are credits.
        var ending = Assert.Single(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"2400","tags":{"title":"Chapter 1"}},{"start_time":"2400","end_time":"2500","tags":{"title":"Credits"}}]}"""));
        Assert.Equal(MarkerType.Credits, ending.Type);
    }

    /// <summary>A two-hour film whose "Ending" chapter starts at 75%: that's the climax, with 18 minutes still to go.</summary>
    [Fact]
    public void FromChapters_IgnoresCreditsTitlesWellBeforeTheEnd()
    {
        const string json = """
            {"chapters":[
              {"start_time":"0","end_time":"5400","tags":{"title":"Chapter 1"}},
              {"start_time":"5400","end_time":"6120","tags":{"title":"Ending"}},
              {"start_time":"6120","end_time":"7200","tags":{"title":"Chapter 3"}}]}
            """;

        Assert.Empty(MarkerDetector.FromChapters(json));
        // Credits followed by a short post-credits scene are still credits.
        var credits = Assert.Single(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"6600","tags":{"title":"Chapter 1"}},{"start_time":"6600","end_time":"7020","tags":{"title":"Credits"}},{"start_time":"7020","end_time":"7200","tags":{"title":"Post-credits"}}]}"""));
        Assert.Equal(MarkerType.Credits, credits.Type);
    }

    [Fact]
    public void FromChapters_IgnoresNumberedChapters() =>
        Assert.Empty(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"600","tags":{"title":"Chapter 1"}}]}"""));
}
