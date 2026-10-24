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
| `~/.flexarr/data/config.json` | Your settings: API key, path mappings — never touched by deploys |
| `~/.flexarr/logs/flexarr.log` | Server log |
| `~/Library/LaunchAgents/io.github.flexarr.server.plist` | Service definition (starts at login, restarts on crash) |

The server listens on port **8383** on all interfaces: `http://<mac-name>.local:8383`.

## Settings (`config.json`)

```json
{
  "apiKey": "generated-on-first-run",
  "authenticationRequired": "Enabled",
  "pathMappings": [
    { "from": "/data", "to": "/Volumes/Media/data" }
  ]
}
```

- **`pathMappings`**: when Sonarr/Radarr/Lidarr run in Docker they report container paths (`/data/tv/…`). Map them to the real paths on the Mac.
- **`authenticationRequired`**: `Enabled` (default) or `DisabledForLocalAddresses`.

Restart after editing: `launchctl kickstart -k gui/$(id -u)/io.github.flexarr.server`.

## Connecting *arr apps running in Docker

Use `host.docker.internal` to reach the Mac from inside containers:

```
http://host.docker.internal:8383/api/v1/webhooks/sonarr?apikey=<apiKey>
```

## Managing the service

```bash
launchctl kickstart -k gui/$(id -u)/io.github.flexarr.server   # restart
launchctl bootout gui/$(id -u)/io.github.flexarr.server        # stop / uninstall
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/io.github.flexarr.server.plist  # start
```

A LaunchAgent runs while the user is logged in, so enable automatic login on a headless server (Docker Desktop has the same requirement).

> **External drives:** macOS may ask for permission the first time Flexarr reads a library on an external volume. If scans can't see your media, grant `~/.flexarr/app/Flexarr` access under System Settings → Privacy & Security → Files & Folders (or Full Disk Access).
