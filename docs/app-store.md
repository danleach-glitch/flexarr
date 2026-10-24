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

- **Subtitle:** Your media server, anywhere
- **Category:** Entertainment. **Price:** free, every country except mainland China (which needs an ICP filing).
- **Age rating:** 4+. The app has no content of its own; what's on the viewer's server is theirs.
- **Keywords:** media server, home server, self-hosted, streaming, video player, movies, tv shows,
  library, player, films. Never other apps' names: App Review rejects them.
- **Support URL:** GitHub issues. **Marketing URL:** https://flexarr.io.
  **Privacy policy:** https://flexarr.io/privacy (`site/privacy.html`).

## App Privacy

"Data Not Collected": the app only talks to the viewer's own server, never to the developer or
third parties. The bundled `PrivacyInfo.xcprivacy` declares UserDefaults (reason CA92.1) and no tracking.

## The demo server for App Review

Reviewers must be able to sign in, or the app is rejected under guideline 2.1. A separate Flexarr runs
for them on the maintainer's server, with only Blender's openly licensed films (CC BY): Big Buck Bunny,
Elephants Dream, Sintel and Tears of Steel (with subtitles).

- Listens on 127.0.0.1:8384 only (LaunchAgent `io.github.flexarr.demo`, files in `~/flexarr-demo`), and
  is published by `tailscale funnel --bg --https=10000 http://127.0.0.1:8384`.
- No \*arr or Seerr connections: Radarr was connected just long enough to fetch the films' details.
- Reviewers sign in with a device sign-in for the "App Review" profile, which can browse and watch but
  not manage the server. The main profile has a PIN, so they can't switch to it.
- Screenshots for the listing come from this server, never from a personal library: other people's
  posters get apps rejected.

Review notes:

> Flexarr is a client for a self-hosted media server, like the Plex or Jellyfin apps. It contains no
> content and offers no way to obtain any. We've set up a demo server with openly licensed films
> (Blender Foundation, CC BY). On the Connect screen, tap "Enter an address instead", enter the
> server address (the user name below) and the API key (the password below), then tap Connect.

## Screenshots

6.9" iPhone (iPhone 17 Pro Max, 1320×2868) and 13" iPad (iPad Pro 13-inch, 2064×2752), from the UI test
tour (`FlexarriOSUITests/ScreenshotTour.swift`) run against the demo server.
