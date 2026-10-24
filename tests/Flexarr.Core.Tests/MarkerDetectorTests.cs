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

    [Fact]
    public void FromChapters_IgnoresNumberedChapters() =>
        Assert.Empty(MarkerDetector.FromChapters("""{"chapters":[{"start_time":"0","end_time":"600","tags":{"title":"Chapter 1"}}]}"""));
}
