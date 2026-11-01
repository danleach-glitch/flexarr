---
title: Use an AI assistant
description: Connect Claude or another AI assistant to Flexarr over MCP, so it can search your library, see what's playing and play things on your TV.
---

Flexarr is an MCP server. MCP (the [Model Context Protocol](https://modelcontextprotocol.io)) is a standard way for AI assistants such as Claude to use other apps. Connect one to Flexarr and you can ask it things like "What am I in the middle of?", "Put the next *Traitors* on the living room TV" or "Queue the new *Rest Is History* episodes".

## What an assistant can do

| Tool | What it does |
| --- | --- |
| `search_library` | Finds movies, shows and episodes by title. |
| `continue_watching` | What you're in the middle of, most recent first. |
| `next_up` | The episode to watch next in a show. |
| `list_devices` | The TVs, phones and tablets signed in, whether Flexarr is open on each, and what each is playing. |
| `now_playing` | Everything playing right now on every screen: which device, whose profile, what, and how far in. |
| `play_on_device` | Plays a title on a device. A show plays the episode you're up to; it can shuffle a show or start from the beginning. |
| `control_device` | Pauses, resumes or stops a device. |
| `set_device_sound` | Changes a device's dialogue boost, night mode or speakers, while Flexarr is open on it. |
| `mark_watched` | Marks a movie or episode watched or unwatched; a show or season marks every episode. Unwatched also forgets how far in it was, taking it out of Continue watching. |
| `remove_from_continue_watching` | Takes a movie or episode out of Continue watching without marking it watched. A show stays out until you play another episode of it. |
| `watchlist` | Your watchlist, with whether each title is ready to play, on its way, coming out later, or not available. |
| `add_to_watchlist` | Adds a movie or show to your watchlist: one from your library, or any title by name (that needs Seerr). Says if it was requested too. |
| `remove_from_watchlist` | Takes a title off your watchlist. It doesn't cancel a request. |
| `podcasts` | The podcasts you follow, with how many episodes you haven't heard. |
| `new_podcast_episodes` | The newest episodes you haven't heard. |
| `podcast_up_next` | Your podcast Up next queue. |
| `add_to_up_next` | Queues an episode, at the end or to play next. |
| `live_channels` | Live TV channels with what's on now and next. |
| `watch_channel_on_device` | Puts a channel on a device. |
| `record_programme` | Sets a programme, or every showing of a series, to record. |
| `recordings` | What's set to record, and what's been recorded. |
| `audiobooks_in_progress` | *Next release.* The books you're part way through, most recent first, with how far in. |
| `set_sleep_timer` | *Next release.* Stops what a device is playing after some minutes, or at the end of the book's chapter. |

In the next release, `play_on_device` also plays a book from where you left it, and `mark_watched` marks one finished. See [Audiobooks](audiobooks.md).

The Live TV tools are only useful once Live TV is set up. See [Live TV](live-tv.md).

As with Siri, a device only plays what it's told while Flexarr is open on it. If it isn't, the command waits two minutes for Flexarr to be opened there, and the assistant is told so.

The assistant acts as the main profile, since it signs in with the API key. Its Continue watching, podcasts and marks are the main profile's.

## What you'll need

- Your server's address, such as `http://mini.local:8383` or `http://192.168.1.20:8383`. The MCP address is that with `/api/v1/mcp` on the end.
- The API key, from **Settings › Security** in Flexarr, signed in as the main profile. Showing it asks for the admin PIN.

## Claude Code

In a terminal, run:

```bash
claude mcp add --transport http flexarr http://mini.local:8383/api/v1/mcp --header "X-Api-Key: <your key>"
```

Change `mini.local` to your server's name or address, and `<your key>` to the API key. Then start Claude Code and ask it something about your library.

## Other assistants

Any MCP client that can connect to a server over HTTP (sometimes called "Streamable HTTP") and send a header can use Flexarr. Give it:

- **URL:** `http://<server>:8383/api/v1/mcp`
- **Header:** `X-Api-Key` with your API key

The exact place to enter these differs from app to app, so check your assistant's own help for adding an MCP server.

Some apps, including Claude Desktop's configuration file, only start MCP servers as local programs. For those, a small bridge program that forwards to an HTTP server, such as `mcp-remote`, can connect them to Flexarr's address with the header. Follow the bridge's own instructions; note that some bridges insist on `https://` unless told otherwise.

> **Note**
> Assistants that connect from the vendor's cloud, rather than from your own computer, can't reach a server on your home network. Use an assistant that runs on a computer at home or on your tailnet.

## Keep it safe

> **Important**
> The API key is full access to your server. Anything holding it can change settings, see what everyone watches and delete media through Sonarr and Radarr. Treat it like a password.

- **Keep the assistant on your network.** Use Flexarr's home address, or its Tailscale address away from home (see [Watch away from home](away-from-home.md)). Never put Flexarr on the internet over plain `http://` to reach it from an assistant.
- **Don't share configuration files** that contain the key, or commit them to a code repository.
- **Prefer the header.** Flexarr also accepts the key in the address as `?apikey=`, for tools that can't send headers, but addresses end up in logs and histories more easily.
- **If the key leaks**, choose **Make a new key** in **Settings › Security**. Everything using the old key stops working until you give it the new one.
- **Check before it acts.** An assistant can play, stop, mark things watched and set recordings. Most assistant apps ask before using a tool; leave that on if you'd rather approve each one.
