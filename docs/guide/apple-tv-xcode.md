---
title: Install the Apple TV app with Xcode
description: Build the Flexarr Apple TV app from source and install it on your own Apple TV with a Mac and Xcode, until it arrives on the App Store.
---

The Flexarr Apple TV app is coming to the App Store. Until it's there, you can build it yourself and install it on your own Apple TV with a Mac. It takes about half an hour the first time, most of it downloading Xcode.

If you only want Flexarr on an iPhone or iPad, you don't need any of this: that app is [on the App Store](https://apps.apple.com/app/flexarr/id6826618084).

## What you need

- **A Mac** that can run Xcode 26 or later.
- **Xcode 26 or later**, free from the Mac App Store. Open it once after installing, and when it offers to download platforms, include **tvOS**. (If you skipped that, add it later in **Xcode › Settings › Components**.)
- **XcodeGen**, which makes the Xcode project from the files in the repository. With [Homebrew](https://brew.sh): `brew install xcodegen`.
- **Git**, to download the source. macOS offers to install it the first time you type `git` in Terminal.
- **An Apple ID.** A free one works.
- **An Apple TV running tvOS 18.2 or later**, on the same network as the Mac.

> **Important** With a free Apple ID, apps you install yourself stop opening after **7 days**. Run the install command again to renew it for another week; your settings and sign-in are kept. A paid Apple Developer Program membership makes installs last a year.

## 1. Download the source

In Terminal:

```bash
git clone https://github.com/danleach-glitch/flexarr.git
cd flexarr
```

## 2. Sign Xcode in to your Apple ID

Open Xcode, then **Xcode › Settings › Accounts**, choose **+** and sign in with your Apple ID. Xcode makes a free "Personal Team" for it.

### Find your Team ID

The install command needs your team's ID, a 10-character code like `A1B2C3D4E5`.

- **Paid membership:** it's on [developer.apple.com/account](https://developer.apple.com/account) under **Membership details**.
- **Free Apple ID:** Xcode makes a development certificate for you the first time it signs an app (or when you choose **Manage Certificates… › + › Apple Development** in **Xcode › Settings › Accounts**). The Team ID is the certificate's organisational unit. In Terminal:

```bash
security find-certificate -c "Apple Development" -p | openssl x509 -noout -subject
```

The Team ID is the value after `OU=`.

## 3. Pair the Apple TV with your Mac

1. On the Apple TV, open **Settings › Remotes and Devices › Remote App and Devices**. Leave this screen open.
2. On the Mac, in Xcode, open **Window › Devices and Simulators** (⇧⌘2).
3. Your Apple TV appears under **Discovered**. Select it and choose **Pair**.
4. Type the code the TV shows into Xcode.

Pairing only happens once. Xcode may take a few minutes to get the Apple TV ready the first time.

## 4. Build and install

Choose a bundle ID: a reverse-domain name that's yours, such as `com.yourname.flexarr.tv`. Apple needs every app's ID to be unique, so don't use one that belongs to someone else. Then, in the `flexarr` folder:

```bash
make apple-install TEAM=A1B2C3D4E5 BUNDLE_ID=com.yourname.flexarr.tv
```

This generates the Xcode project, builds the app, signs it with your team, installs it on the first paired Apple TV it finds and opens it. With more than one Apple TV, name the one you want with `DEVICE=` and its identifier, shown in Xcode's **Devices and Simulators** window:

```bash
make apple-install TEAM=A1B2C3D4E5 BUNDLE_ID=com.yourname.flexarr.tv DEVICE=<identifier>
```

The first build takes several minutes. When Flexarr opens on the TV, [connect it to your server](connect-devices.md#apple-tv).

## Updating the app

Download the latest source and run the same command again:

```bash
git pull
make apple-install TEAM=A1B2C3D4E5 BUNDLE_ID=com.yourname.flexarr.tv
```

Use the same bundle ID every time, so the TV treats it as the same app and keeps your sign-in. Keeping the app and the server on the same version avoids surprises; see [Updating](updating.md).

## Troubleshooting

### "No paired Apple TV found"

The Mac can't see a paired Apple TV. Check both are on the same network, the Apple TV is awake, and it's listed (and connected) in Xcode's **Devices and Simulators** window. If it's listed but greyed out, wake the TV and wait a minute.

### `xcodegen: command not found`

Install XcodeGen (`brew install xcodegen`), then open a new Terminal window.

### Signing errors: "No Account for Team", "No profiles were found"

- Check the `TEAM` value is your Team ID, not your email address or name, and that Xcode is signed in to that Apple ID.
- Choose a different `BUNDLE_ID` if Xcode says the identifier isn't available. Someone else has it.

### The app stopped opening after a week

That's the 7-day limit on free Apple IDs. Run `make apple-install` again.

### tvOS platform or SDK missing

Open **Xcode › Settings › Components** and download tvOS.
