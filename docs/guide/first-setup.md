---
title: First setup
description: Open your new Flexarr server in a browser, make yourself its owner, add your films, shows and music, and find the API key if you ever need it.
---

Once the server is installed and running, everything else happens in a web browser. There's no account to make and no key to copy: the first browser on your home network to enter the server's setup code becomes its owner.

## Open Flexarr in a browser

Flexarr listens on port **8383**. On a computer on the same home network as the server, open:

```text
http://<server>:8383
```

Replace `<server>` with the server's address. Any of these usually work:

- On the server itself: `http://localhost:8383`
- The server's name on your network, such as `http://mac-mini.local:8383`
- Its IP address, such as `http://192.168.1.20:8383`. Your router's app or admin page lists the addresses of devices on your network.

If the page doesn't load, check that Flexarr is running (your install guide, linked from [What you need](what-you-need.md), shows how) and that you've typed `http://`, not `https://`.

## Enter the setup code

A new server greets you with **Welcome to your Flexarr server**. The **setup code** proves you're the one who installed it: some setups (a reverse proxy, Docker Desktop) make visitors from the internet look as though they're on your home network, and only someone with the server can read its code. It's eight letters and numbers, like `K7QM-3PXT`.

Usually there's nothing to type, because the server gives you a link with the code filled in:

- **Docker** (including Synology and Unraid): the container's log (`docker logs flexarr`, or the log in your NAS's Docker app) says **Set it up at http://localhost:8383/#setup=…**. Open that link. From another computer, put the server's name or IP address in place of `localhost`. The code alone is in `setup-code.txt` in the container's settings folder.
- **macOS (installed with the script)**: the script ends with a **Set it up** link. The code is also in `~/.flexarr/logs/console.log` and `~/.flexarr/data/setup-code.txt`.
- **The Mac app** (coming to the Mac App Store) opens the welcome page with the code filled in.

Opened without the link, the page asks for the code and says where it is for your kind of install. Pasting the whole link into the **Setup code** box works too.

Then choose the **admin PIN** (4 to 8 digits, typed twice). It protects the server's settings: watching doesn't need it; changing settings does. Choose **Set up Flexarr**. This browser becomes the server's owner and is signed in. That's the whole sign-up.

If you close the page before choosing **Set up Flexarr**, nothing has happened yet, and the same code still works. The code stops working once the server is set up, and after ten wrong tries it's replaced with a new one (the log says so).

![The welcome panel on a new server, asking for its setup code and a PIN, typed twice, beside Set up Flexarr](../../site/guide/img/welcome.webp "What a new server shows a browser on your home network")


A few things to know:

- **It only works once.** As soon as anyone signs in, setup closes for good. Phones, tablets and TVs then connect with a code that you allow from your phone or this browser (see [Connect your devices](connect-devices.md)). Other browsers sign in with the [API key](#the-api-key).
- **It only works from home.** The button appears only to a browser on your home network, reaching the server by its IP address, a `.local` name or the server's own name. That stops anyone on the internet, or a web page you happen to visit, from claiming your server.
- **Older servers** don't need any of this. One set up before version 0.49.0 keeps using the API key, and one without an admin PIN asks you to **Choose an admin PIN** the first time you open Settings.

> **Note** If you see a box asking for an **API key** instead of the welcome, someone has already set this server up, or you're not on the home network. Try opening it by its IP address from a computer at home. If you set it up yourself in another browser, sign in here with the key (see [The API key](#the-api-key) below).

## The setup guide

Next, Flexarr welcomes you with a short setup guide, one card at a time. Choose **Get started**, or **I'll set it up myself** to go straight to Home. Every step after your name can be skipped, and **Set up later** leaves the guide whenever you like:

1. **What's your name?** Type your name and choose a colour for your profile, the one that runs Flexarr.
2. **Connect your apps.** Connect Sonarr, Radarr and Lidarr (and, in the next release, Readarr), if you use them. If Flexarr finds one running nearby, its address is filled in already, and you only paste its API key. Flexarr also adds its own webhook to each app in the background (**Setting up instant updates…**), so new downloads show up straight away (see [Webhooks](connect-arrs.md#webhooks-new-downloads-appear-straight-away)). Connected the wrong one? Choose **Disconnect**. Don't use them? Choose **Skip**.
3. **Add your libraries.** There's a line each for **Movies**, **TV Shows** and **Music** (and **Audiobooks**, in the next release, when Readarr is connected). A folder from Sonarr, Radarr or Lidarr is filled in already. Otherwise choose **Choose folder**, go to the folder that holds it and choose **Add library**. Then choose **Next**, or **Continue** once they're added. Choose a whole drive or your home folder and it warns you: reading all of it takes hours. The guide adds one library of each kind; more, and home videos, are in **Settings › Libraries**.
4. **Who's watching?** Add a profile for each person in the house, or choose **Skip**. Added someone by mistake? Choose the **✕** on their picture. See [Profiles](profiles.md).
5. **Watch on all your devices.** Each app is listed with where to get it. Open it at home, and when it asks to connect, choose **Allow**. See [Connect your devices](connect-devices.md).
6. **Watch away from home.** Turn on Tailscale, and **Disconnect** to turn it off again. Not installed yet? **Get Tailscale** opens its download page: install it on the server, sign in, then choose **Check again**. In Docker, see [Watch away from home](away-from-home.md) for other ways.

At the end, the guide shows what's set up, says **Checking your libraries** while it reads your files for the first time, and warns you if a library came up empty or Flexarr can't see one of an app's folders.

If you leave part way, Home lists what's left under **Finish setting up**. Each one opens the right page in Settings; **Hide** puts the list away. Everything in the guide is in Settings too, as described below.

You're signed in as the server's **main profile**, the one that can change settings.

## Add your libraries

A library is a set of folders holding one kind of media. Go to **Settings › Libraries** and choose **Add library**.

![Settings › Libraries listing each library with its folder and a Fine for kids switch](../../site/guide/img/libraries.webp "Settings › Libraries")

1. Pick the **Kind**: **Films**, **TV shows**, **Music**, **Home videos** (videos of your own, arranged by folder; see [Without Sonarr and Radarr](without-arrs.md#home-videos)) or, in the next release, **Audiobooks** (see [Audiobooks](audiobooks.md)).
2. Give it a **Name** if you like. Left blank, it's named after its kind. You can rename it later.
3. Under **Folders**, browse the server's disks and choose **Use this folder** on the folder that holds that media. Add more than one folder if your films are spread across drives.
4. Choose **Add library**.

The setup guide does this for you; this is how to add one later. Add one library for each kind of media you have. Keep films and TV shows in separate folders: Flexarr expects a films folder to hold films and a TV folder to hold shows.

You can have more than one library of a kind, such as **TV** and **Anime**, or **Films** and **Films in 4K**. Home still shows one **Latest unwatched** row for all your films and one for all your shows, and a title that's in two libraries shows once. To give each library its own row, turn off **Combine libraries of the same kind** in **Settings › Home & browse**.

> **Important** The folder browser shows the disks as the *server* sees them. In Docker, that means the paths inside the container, such as `/data/media/movies`, not the paths on your NAS. If you can't find your media, check the volumes you gave the container in your install guide.

### Podcasts are built in

You don't need to add a podcasts library. There's always one, listed under **Podcasts** at the bottom of **Settings › Libraries**. Its **Episodes folder** is where episodes wait on their way to phones; you can move it with **Change**. See [Podcasts](podcasts.md).

## The first scan

Adding a library starts a scan straight away. You'll see its progress in **Settings › Libraries**, and titles appear on Home and in the sidebar as they're found.

How long it takes depends on the size of your library and how fast the disks are, and artwork keeps arriving for a while after the files are found. You can start watching as soon as something shows up.

After that, Flexarr rescans every 12 hours, and straight away whenever Sonarr, Radarr or Lidarr tell it about a new download. Each library has buttons to scan it now, refresh its artwork and details, rename it or remove it. Removing a library never deletes your files.

> **Tip** Films and TV shows get their artwork and details through Radarr and Sonarr. If you run them, [connect them](connect-arrs.md) next. Without them, films and episodes still play, named from their files, with artwork and details from any `.nfo` files and images (`poster.jpg`, `fanart.jpg`) beside them, from TVmaze for shows, and from TMDB for films if you add a free key. See [Without Sonarr and Radarr](without-arrs.md). Music reads the tags and cover images in your files, and Lidarr adds more.

## The API key

Flexarr still has an API key. You won't need it for everyday use at home, but it's there for:

- signing in another browser, or an app away from home when you don't use [Tailscale](away-from-home.md)
- scripts, [Home Assistant](home-assistant.md) and [assistants](assistant-mcp.md)
- getting back in if you've lost every signed-in browser

Signed in as the main profile, you can see it in **Settings › Security** under **API key**: choose the eye to show it (with the admin PIN). **Make a new key** replaces it if it's ever got out.

It's also in the server's settings file, `config.json`, as `apiKey`:

| Installed with | Where `config.json` is |
| --- | --- |
| Docker (including Synology and Unraid) | `/config/config.json` inside the container, which is the folder you mounted at `/config` |
| macOS | `~/.flexarr/data/config.json` |

> **Important** Anyone with the API key can manage your server. Don't post it in screenshots or bug reports.

## Next steps

- [Connect your devices](connect-devices.md): your iPhone, iPad, Apple TV and other browsers
- [Connect Sonarr, Radarr, Lidarr and Seerr](connect-arrs.md)
- [Profiles](profiles.md) for everyone in the house
