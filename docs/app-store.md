# Publishing the iPhone and iPad app

One universal app (FlexarriOS) covers iPhone and iPad. It's listed in App Store Connect as "Flexarr"
with the bundle ID `com.danleach.flexarr.ios`.

## Uploading a build

    make ios-upload TEAM=<team id> BUNDLE_ID=com.danleach.flexarr.ios

Xcode must be signed in to the paid developer account (Xcode › Settings › Apple Accounts). Each upload
gets the current time as its build number. It appears under TestFlight after processing (10–30 minutes).
The version shown on the App Store is `MARKETING_VERSION` in `clients/apple/project.yml`, the same
as the server's.

## Listing

The listing pitches Flexarr as your own streaming service: everything you love, with no ads, tracking or
subscription. The server requirement is put positively (it connects to your own free Flexarr server), never
as what the app lacks.

- **Name:** Flexarr: Personal Streaming. **Subtitle:** Films, TV, music and podcasts (en-US: Movies, TV,
  music & podcasts). Listed in English (UK) and English (US).
- **Category:** Entertainment. **Price:** free, every country except mainland China (which needs an ICP filing).
- **Age rating:** 4+. The app has no content of its own; what's on the viewer's server is theirs.
- **Keywords:** media server, home server, free, no ads, no account, self-hosted, home cinema (en-US: home
  theater), offline, player, movies, shows. Words in the name and subtitle are indexed already, so they
  aren't repeated. Never other apps' names: App Review rejects them.
- **Support URL:** GitHub issues. **Marketing URL:** https://flexarr.io.
  **Privacy policy:** https://flexarr.io/privacy (`site/privacy.html`).

## App Privacy

Since 0.49.0 (Sentry error reports): Diagnostics › Crash Data, Performance Data (app hangs) and Other
Diagnostic Data, each for App Functionality, not linked to the user and not used for tracking. They're off
unless the server's owner turns them on, but an on/off switch doesn't meet Apple's "optional disclosure"
exemption, so they're declared. Nothing else goes to the developer or third parties: the push relay keeps
nothing. The bundled `PrivacyInfo.xcprivacy` declares UserDefaults (reason CA92.1) and no tracking; it
should list the same three data types.

## The demo server for App Review

Reviewers must be able to sign in, or the app is rejected under guideline 2.1. A separate Flexarr runs
for them on the maintainer's server, with only Blender's openly licensed films (CC BY): Big Buck Bunny,
Elephants Dream, Sintel and Tears of Steel (with subtitles).

- Listens on 127.0.0.1:8384 only (LaunchAgent `io.github.flexarr.demo`, files in `~/flexarr-demo`), and
  is published by `tailscale funnel --bg --https=10000 http://127.0.0.1:8384`.
- No \*arr or Seerr connections: Radarr was connected just long enough to fetch the films' details.
- Reviewers sign in with a device sign-in for the "App Review" profile, which can browse and watch but
  not manage the server. The main profile has a PIN, so they can't switch to it.

Review notes:

> Flexarr is a client for a self-hosted media server, like the Plex or Jellyfin apps. It contains no
> content and offers no way to obtain any. We've set up a demo server with openly licensed films
> (Blender Foundation, CC BY). Tap Skip on the welcome cards. On the Connect screen, tap "Enter an
> address instead" and enter the server address (the user name below). Tap "Connecting from outside your
> home? Use an API key", enter the API key (the password below), tap Connect, and choose the "App
> Review" profile.

`FlexarriOSUITests/ReviewerSignIn.swift` walks these steps on a fresh install; run it against the review
server before submitting a version whose connect screens changed.

## Screenshots and preview

6.9" iPhone (iPhone 17 Pro Max, 1320×2868) and 13" iPad (iPad Pro 13-inch, 2064×2752). The screens come
from `FlexarriOSUITests/StoreTour.swift` run against a real library (the maintainer chose to show real
artwork, accepting the risk under guideline 5.2), then are set in device frames with a headline over
artwork-tinted backgrounds. Point the simulator at a local address (an SSH tunnel to `127.0.0.1` works):
the app refuses plain http to anything else. The preview video is a simulator recording of
`FlexarriOSUITests/PreviewTour.swift`.

## Apple TV

The Apple TV app is on the same listing as the iPhone and iPad app (universal purchase), so it ships with the iOS
bundle ID: `make tv-upload TEAM=<team id> BUNDLE_ID=<the iOS bundle id>` (the Top Shelf extension becomes
`<bundle id>.topshelf`). It has its own version, text, screenshots (1920×1080) and review submission; the name,
subtitle, age rating and App Privacy are shared.

- tvOS can't open links, so the listing needs the privacy policy as plain text (App Information › Privacy Policy
  Text). Until it's there, App Store Connect refuses to submit any platform's version. Keep it in step with
  `site/privacy.html`.
- Reviewers sign in with the Siri Remote: "Enter an address instead", the review server's address, "Connecting
  from outside your home? Use an API key" and the key, then the App Review profile. `FlexarrTVUITests/ReviewerSignIn.swift` walks it on a fresh install, with the same
  environment as the iOS test.
- The screens behind the screenshots come from `FlexarrTVUITests/StoreTour.swift`.
