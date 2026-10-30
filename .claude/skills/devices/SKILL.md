---
name: devices
description: Install the Flexarr apps on the user's Apple TV, iPhone and Android phone, and upload to TestFlight. Use after a release or a deploy that changed clients/apple or clients/android, or when the user asks to update their devices.
---

# /devices [version]

Device IDs (and the Android phone's adb serial), team and bundle IDs are in CLAUDE.local.md. Build from the same commit as the server on the Mini (a release tag, or origin/main), from a clean checkout in the scratchpad.

**Version:** at a release, the tag's version is already in project.yml. Between releases, pass the next version with `MARKETING=<x.y.z>` (the Mini's dev version without its `-dev…` part): App Store Connect refuses TestFlight uploads of a version that's already on the App Store. The Android build takes the same version as `-Pflexarr.version=<x.y.z>` (without it, it's the server's last released version).

Run each step in the background, one at a time (the Apple ones share build/apple):

1. **Apple TV:** `make apple-install TEAM=… BUNDLE_ID=com.danleach.flexarr.tv DEVICE=<TV id> [MARKETING=…]`.
2. **iPhone:** `make ios-install TEAM=… BUNDLE_ID=com.danleach.flexarr.ios DEVICE=<iPhone id> [MARKETING=…]`. Exit code 2 after "installed" only means the phone was locked so the app couldn't launch: confirm with `xcrun devicectl device info apps --device <id> | grep -i flexarr`. If the phone isn't reachable, say so and carry on.
3. **Android phone:** in `clients/android` of the clean checkout, `./gradlew :app:assembleRelease [-Pflexarr.version=…]` (signed with the upload key from `~/.flexarr-release`, so the clean checkout needs nothing copied in), then `adb -s <serial> install -r app/build/outputs/apk/release/app-release.apk`. Confirm with `adb -s <serial> shell dumpsys package com.danleach.flexarr | grep versionName`. Always `-s`: an emulator may be running too. If the phone isn't in `adb devices` (unplugged, or USB debugging off), say so and carry on. Google Play uploads happen only at a release.
4. **TestFlight:** `make ios-upload TEAM=… BUNDLE_ID=com.danleach.flexarr.ios [MARKETING=…]`, and `make tv-upload …` for the Apple TV build when the user wants TV testers to get it. Uploads are real-world actions: confirm with the user first unless they asked for TestFlight in this conversation.
5. **Report in one line** which devices now run which version, and anything that didn't install.

App Store submissions happen only at a release, and the user does them in App Store Connect.
