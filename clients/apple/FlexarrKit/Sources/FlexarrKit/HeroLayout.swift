import Foundation

/// The sizes a hero (Home's, a library's, a title page's backdrop) takes on iPhone, iPad and the Mac.
public enum HeroLayout {
    /// A featured hero's height. Shorter on a phone held sideways, so the title and Play stay on screen. On a wide
    /// screen at most a little over half the window's height (`window`, the scroll view's), so the first row's heading
    /// shows beneath it on an iPad held sideways; never so short the title and buttons crowd.
    public static func featuredHeight(regularWidth: Bool, compactHeight: Bool, window: Double) -> Double {
        if compactHeight { return 340 }
        guard regularWidth else { return 560 }
        guard window > 0 else { return 620 }
        return min(620, max(380, (window * 0.55).rounded()))
    }

    /// How strongly the fade under the status bar and back button shows, 0 to 1, once a page has scrolled `scrolled`
    /// points: none while the hero (`heroHeight` tall) is still behind the bars, all of it by the time its title would
    /// reach them (`topInset` is the bars' height). Steps of a tenth, so scrolling redraws it only now and then.
    public static func topFade(scrolled: Double, heroHeight: Double, topInset: Double) -> Double {
        let start = max(0, heroHeight - topInset - 220)
        let fraction = min(1, max(0, (scrolled - start) / 80))
        return (fraction * 10).rounded() / 10
    }
}
