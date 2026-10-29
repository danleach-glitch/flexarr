# Publishing the Android app

One app (`clients/android`) covers Android phones and tablets. It's listed on Google Play as "Flexarr: Personal
Streaming" with the application ID `com.danleach.flexarr`, which can never change once it's on Play.

## Signing

Play App Signing: Google keeps the app's signing key and signs what people download. We sign uploads with an
**upload key**, which Google can replace if it's lost (Play Console › Test and release › App integrity).

The upload key is `~/.flexarr-release/android-upload.jks`, described by `~/.flexarr-release/android-upload.properties`
(`storeFile`, `storePassword`, `keyAlias`, `keyPassword`). Keep both in 1Password too. `FLEXARR_ANDROID_SIGNING` points
at another properties file. Without one, `bundleRelease` builds an unsigned bundle, so forks still build.

## Uploading a build

    make android-bundle                 # the signed bundle, clients/android/app/build/outputs/bundle/release
    make android-upload                 # to the internal testing track
    make android-upload TRACK=alpha     # to the closed test
    make android-listing                # the store listing below

Uploads use Gradle Play Publisher. It signs in to Play Console with a service account's JSON key, at
`~/.flexarr-release/play-service-account.json` (or its contents in `ANDROID_PUBLISHER_CREDENTIALS`). To make one:
Google Cloud › the Play Console's project › IAM › Service accounts › create, then Keys › Add key › JSON. Then invite its
email in Play Console › Users and permissions, with Release and Store presence permissions for the app. Without a key the
plugin stays off, and `android-bundle` still works.

The version is the server's (`Directory.Build.props`): 0.54.0 is versionName `0.54.0`, versionCode 54000. Play refuses
a version code it has seen before, so with the key present a taken one is raised past the highest on Play. Builds from
main between releases pass the next version, as for TestFlight: `make android-upload MARKETING=0.55.0`.

The very first upload has to go through the Play Console website (Test and release › Testing › Internal testing ›
Create release, then drop in the `.aab`). Play only lets the API upload once the app has a build.

## Listing

The text and graphics live in `clients/android/app/src/main/play` (Gradle Play Publisher's layout), in English (UK):
`listings/en-GB/title.txt` (30 characters), `short-description.txt` (80), `full-description.txt` (4,000), and
`graphics/icon` (512×512) and `graphics/feature-graphic` (1024×500). `release-notes/en-GB/default.txt` goes with each
upload. As on the App Store, the listing pitches your own streaming service, and the server is put positively.

- **Category:** Entertainment. **Price:** free, in every country Play offers. **Contains ads:** no.
- **Contact:** website https://flexarr.io; the email is set in Play Console only, so it stays out of this repo.
- **Privacy policy:** https://flexarr.io/privacy.
- **Screenshots:** phone (1080×1920, 9:16; Play refuses anything longer than 2:1, so not a modern phone's full
  screen), 7" tablet (1200×1920) and 10" tablet (1600×2560), in `graphics/phone-screenshots`, `seven-inch-screenshots`
  and `ten-inch-screenshots`, numbered in the order shown. They're from the real library on the Mini's Showcase
  profile (the maintainer's choice, as for the App Store), taken on emulators made to those sizes
  (`Flexarr_Store_Phone`, `Flexarr_Store_Tab7`, `Flexarr_Store_Tab10`) with the debug launch arguments, `noHistory`,
  and the status bar in demo mode (9:41, full Wi-Fi and battery). Keep out anything an "Everyone" rating or Google's IP
  policy would trip on: rude gestures, suggestive posters, and Live TV's channel list (it shows the household's TV
  source and broadcasters' logos). The player is The Grand Budapest Hotel at about 1:05:00.

## Data safety

What leaves the phone for anyone but the user's own server:

- **Crash logs** and **Diagnostics** (Sentry error reports): collected, not shared, optional (off unless the server's
  owner turns them on), for App functionality, encrypted in transit, not linked to the user.
- **Device or other IDs** (the Firebase id for notifications, given only once they're turned on): collected, not shared,
  optional, for App functionality, encrypted in transit.
- Nothing else. Viewing history, profiles and logs go to the user's own server, which isn't the developer's, so they
  aren't "collected". There's no account, so no account deletion to offer: the answer is that users can't create one.

Keep this in step with `site/privacy.html` and the App Store's App Privacy.

## Other declarations

- **Content rating** (IARC questionnaire): answer no to every content question, as the app contains no media of its own
  (the same reasoning as the App Store's 4+). It lets no users talk to each other and shares no location.
- **Target audience:** 18 and over is the simplest (anything younger brings the Families policy). The app is for
  everyone in a household, but children reach it through a parent's server.
- **App access:** reviewers need a sign-in. Use the App Review demo server from `docs/app-store.md`, with the same
  steps: "Enter an address instead", the server's address, "Connecting from outside your home? Use an API key", the
  key, then the "App Review" profile.
- **Foreground services** (Policy › App content): Play asks why each type is used, with a short video.
  - *Media playback:* podcasts and music play on with the screen off, with controls in the notification and on the
    lock screen. Video: play a podcast episode, lock the phone, show the lock screen controls.
  - *Data sync:* downloads for offline viewing carry on with the app closed, shown as a notification with progress.
    Video: start a film's download, leave the app, show the notification. If Play pushes back, the alternative is a
    user-initiated data transfer job (Android 14 on).
- **Android Auto:** opt in under Test and release › Advanced settings › Form factors. Music and podcasts in the car go
  through Google's own Android Auto review, which can take longer than the app's.
- **News app, health, financial features, government app:** no.

## Testing before production

A personal developer account created since November 2023 must run a **closed test with at least 12 testers, opted in
for 14 days in a row**, before it can apply for production access. Testers join with a Google account, from a Google
Group or an email list, and need the opt-in link. Start it the day the account is approved:

1. Upload to internal testing first (up to 100 testers, no review) to check the build installs from Play.
2. Create the closed test (`alpha` track), add the testers, and upload to it: `make android-upload TRACK=alpha`.
3. After 14 days with 12 opted in, apply for production in the Dashboard. Google asks a few questions about the test.

When the closed test opens, `site/apps.json` can move Android to `beta` with its opt-in link (see CLAUDE.md, *The apps
page*).
