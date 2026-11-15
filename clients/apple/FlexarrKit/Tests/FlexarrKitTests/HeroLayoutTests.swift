import Testing
@testable import FlexarrKit

struct HeroLayoutTests {
    @Test func phoneHeightsStayAsTheyWere() {
        #expect(HeroLayout.featuredHeight(regularWidth: false, compactHeight: false, window: 874) == 560)
        #expect(HeroLayout.featuredHeight(regularWidth: false, compactHeight: true, window: 402) == 340)
        #expect(HeroLayout.featuredHeight(regularWidth: true, compactHeight: true, window: 440) == 340)
    }

    @Test func iPadPortraitKeepsTheTallHero() {
        #expect(HeroLayout.featuredHeight(regularWidth: true, compactHeight: false, window: 1194) == 620)
    }

    @Test func iPadLandscapeLeavesRoomForTheFirstRow() {
        // An 11-inch iPad held sideways: 834 points, so a row's heading shows below the hero.
        let height = HeroLayout.featuredHeight(regularWidth: true, compactHeight: false, window: 834)
        #expect(height == 459)
        #expect(height + 28 + 40 < 834)
    }

    @Test func aShortWindowNeverCrushesTheHero() {
        #expect(HeroLayout.featuredHeight(regularWidth: true, compactHeight: false, window: 500) == 380)
        #expect(HeroLayout.featuredHeight(regularWidth: true, compactHeight: false, window: 0) == 620)
    }

    @Test func topFadeWaitsForTheHeroToScrollAway() {
        // A phone's title page: a 420 point hero under 103 points of status bar and back button.
        #expect(HeroLayout.topFade(scrolled: 0, heroHeight: 420, topInset: 103) == 0)
        #expect(HeroLayout.topFade(scrolled: 97, heroHeight: 420, topInset: 103) == 0)
        #expect(HeroLayout.topFade(scrolled: 137, heroHeight: 420, topInset: 103) == 0.5)
        #expect(HeroLayout.topFade(scrolled: 177, heroHeight: 420, topInset: 103) == 1)
        #expect(HeroLayout.topFade(scrolled: 2000, heroHeight: 420, topInset: 103) == 1)
    }

    @Test func topFadeShowsAtOnceWithoutAHero() {
        #expect(HeroLayout.topFade(scrolled: 0, heroHeight: 0, topInset: 103) == 0)
        #expect(HeroLayout.topFade(scrolled: 80, heroHeight: 0, topInset: 103) == 1)
    }
}
