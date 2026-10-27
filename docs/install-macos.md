# Running Flexarr on macOS

Flexarr runs natively on macOS (Apple silicon or Intel) as a per-user LaunchAgent. Native rather than Docker because Docker on macOS can't use VideoToolbox hardware transcoding.

## Deploy from a development machine

```bash
make deploy                    # deploys to the SSH host "mini"
make deploy DEPLOY_HOST=other  # or any other Mac you can `ssh` into
make logs                      # tail the server log
```

`scripts/deploy-macos.sh` builds the web client, publishes a self-contained server (no .NET needed on the target), copies it over SSH and (re)starts the LaunchAgent. It waits for `/health` before reporting success.

| Path on the target | Contents |
| --- | --- |
| `~/.flexarr/app` | Binaries — replaced on every deploy |
| `~/.flexarr/data/config.json` | Your settings: API key, path mappings — never touched by deploys. Readable only by you |
| `~/.flexarr/data/devices.json` | Signed-in browsers and Apple TVs (hashes of their tokens). Delete it to sign them all out |
| `~/.flexarr/logs/flexarr-<date>.log` | Server log, one file a day (see [Logs](logging.md)). `make logs` tails it |
| `~/.flexarr/logs/console.log` | Warnings and errors on standard output: look here when the server won't start |
| `~/Library/LaunchAgents/io.github.flexarr.server.plist` | Service definition (starts at login, restarts on crash) |

The server listens on port **8383** on all interfaces: `http://<mac-name>.local:8383`.

## Settings (`config.json`)

```json
{
  "apiKey": "generated-on-first-run",
  "webhookSecret": "generated-on-first-run",
  "imageSecret": "generated-on-first-run",
  "authenticationRequired": "Enabled",
  "pathMappings": [
    { "from": "/data", "to": "/Volumes/Media/data" }
  ],
  "trustedProxies": [],
  "knownHosts": []
}
```

- **`pathMappings`**: when Sonarr/Radarr/Lidarr run in Docker they report container paths (`/data/tv/…`). Map them to the real paths on the Mac.
- **`authenticationRequired`**: `Enabled` (default) or `DisabledForLocalAddresses`, which lets devices on your network in as the main profile without a key.
- **`webhookSecret`**: what Sonarr/Radarr/Lidarr webhooks send. It can only start scans.
- **`imageSecret`**: signs the links to outside artwork (Coming soon, requests, collections) that Flexarr hands to its apps, so nobody else can use it to fetch images. Leave it alone; changing it only breaks links already handed out until the page reloads.
- **`trustedProxies`**: a reverse proxy on another machine or in Docker (`"172.17.0.1"`, or a range like `"10.0.0.0/24"`), whose `X-Forwarded-For` Flexarr should believe. Without it, everything forwarded by such a proxy counts as the internet. Read at startup.
- **A reverse proxy on this Mac** (nginx, Caddy) must send `X-Forwarded-For` (in nginx, `proxy_set_header X-Forwarded-For $remote_addr;`). One that doesn't makes every visitor look like the Mac itself, which `DisabledForLocalAddresses` lets in as the main profile. **`loopbackNeedsForwarding`**: `true` treats requests from this Mac that don't say who they're forwarding for as the internet, so such a proxy can't let anyone in (a browser on the Mac then needs the key too).
- **`legacySignInsUntil`**: written on first run; until then, browsers and profile choices from versions before per-device sign-ins keep working while they move over. After it, they sign in again.
- **`knownHosts`**: extra names you reach Flexarr by on your network (split DNS, say). With `DisabledForLocalAddresses`, only requests addressed to IP addresses, `.local`-style names, this Mac's name and these sign in without a key.

Over plain HTTP the API key and sign-ins cross the network unencrypted. Keep `http://` to a home network you trust; from anywhere else use Tailscale (Settings › Remote access), which encrypts the connection and gives Flexarr an HTTPS address.

Restart after editing: `launchctl kickstart -k gui/$(id -u)/io.github.flexarr.server`.

## Connecting *arr apps running in Docker

Use `host.docker.internal` to reach the Mac from inside containers:

```
http://host.docker.internal:8383/api/v1/webhooks/sonarr?secret=<webhookSecret>
```

## Managing the service

```bash
launchctl kickstart -k gui/$(id -u)/io.github.flexarr.server   # restart
launchctl bootout gui/$(id -u)/io.github.flexarr.server        # stop / uninstall
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/io.github.flexarr.server.plist  # start
```

A LaunchAgent runs while the user is logged in, so enable automatic login on a headless server (Docker Desktop has the same requirement).

> **External drives:** macOS may ask for permission the first time Flexarr reads a library on an external volume. If scans can't see your media, grant `~/.flexarr/app/Flexarr` access under System Settings → Privacy & Security → Files & Folders (or Full Disk Access).
