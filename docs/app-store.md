# Publishing the Apple apps

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

## Mac (Flexarr Server)

The Mac app (`clients/macos`) is the server itself, so it has its own listing rather than joining the iPhone and Apple TV
one: "Flexarr Server", bundle ID `com.danleach.flexarr.mac`, SKU `flexarr-mac`. A universal purchase would have shared
the player apps' name, subtitle and App Privacy, and couldn't be split apart later.

    make mac-upload TEAM=<team id> BUNDLE_ID=com.danleach.flexarr.mac

It needs the Mac App Distribution and Mac Installer Distribution certificates, which Xcode creates when it's signed in
to the developer account. `MAC_EXPORT=export` makes the signed package in `clients/macos/build/export` without
uploading, to check signing. One package runs on Apple silicon and Intel (macOS 14 and later). Upload only a release:
the App Store version must match `MARKETING_VERSION` in `clients/macos/project.yml`.

### Listing

- **Subtitle:** Your own home media server. **Category:** Entertainment. **Price:** free. **Age rating:** 4+.
- **Keywords:** streaming, movies, films, tv shows, music, podcasts, self-hosted, library, transcode, free, no ads, offline.
- **Promotional text:** Turn your Mac into a streaming service for the whole household: your films, shows, music and
  podcasts on iPhone, iPad, Apple TV and the web. Free, with no ads.
- The description leads with your own streaming service, then the Mac's part: hardware video conversion, the menu bar,
  start at login, only the folders you choose, Apple silicon and Intel. It ends saying Flexarr contains no media of its own.
- **URLs and App Privacy:** as for the iPhone app. The server is what sends the optional error reports, so the same
  three Diagnostics types are declared. `FlexarrServer/PrivacyInfo.xcprivacy` lists them, and the reasons for the
  APIs the app and its bundled server, FFmpeg and SQLite use: UserDefaults (CA92.1), file timestamps (C617.1, 3B52.1)
  and disk space (E174.1, 85F4.1).

### App Review

No demo server is needed: the reviewer runs the server. Review notes:

> Flexarr Server is a personal media server, like Plex Media Server or Jellyfin. It contains no content and offers no
> way to obtain any; it plays video files the user already has.
>
> It's a menu bar app with no Dock icon or window. After launch, click the Flexarr icon in the menu bar and choose
> "Open Flexarr" to open its interface in the browser. The first time, choose "Set up Flexarr": that browser becomes the
> server's owner, with no account or password to create.
>
> To test: in Flexarr, choose Add library, then "Choose a folder on the Mac…" and select a folder holding a video file.
> An openly licensed film you can use is Big Buck Bunny (Blender Foundation, CC BY), a 65 MB zip to double-click
> open: https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_320x180.mp4.zip. The film appears in the
> library in a few seconds, and plays in the browser. The free Flexarr app for iPhone and Apple TV (already on the App Store) finds
> this server automatically on the same network.
>
> Connections to other library tools are optional, and the app works fully without them.

Blender moves its downloads now and then: check the link, and walk these steps on a clean install, before submitting a version that changes first-run or Add library.
