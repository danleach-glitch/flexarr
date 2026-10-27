# Configuration (`config.json`)

Most things are set in the web app's Settings. A few live only in `config.json`, in Flexarr's data folder (`/config` in Docker, `~/.flexarr` on a Mac). Flexarr reads it at startup.


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

