# Flexarr tunnel

A small Go library on [tsnet](https://pkg.go.dev/tailscale.com/tsnet) that lets the iPhone, Apple TV and
Android apps reach a home server as a remote guest. It runs a Tailscale node inside the app (userspace
networking, no VPN profile) and forwards a loopback port to one address on the tailnet, so the app just uses
`http://127.0.0.1:<port>`. See `docs/remote-guests.md`.

Log upload to Tailscale is off (logtail isn't compiled into release builds, and it's disabled at start-up
anyway).

## API

Go (`package tunnel`), also what gomobile binds for Kotlin as `io.github.flexarr.tunnel.Tunnel`:

```go
func Start(stateDir, hostname, authKey, target, controlURL string) (port int, err error)
func Status() string // {"state":"running|starting|stopped|needs-login|error","path":"direct|peer-relay|derp|unknown","error":"..."}
func Stop()
func SetVerbose(on bool)
```

- `stateDir` holds the node's identity. Keep it across launches but out of backups. The auth key is
  needed only the first time; later starts can pass `""`.
- `target` is `ip:port` on the tailnet, such as `100.101.102.103:8383`. `controlURL` is `""` for
  Tailscale's control server (it's only set in tests).
- `Start` blocks until the node is on the tailnet (up to a minute), so call it off the main thread. Calling
  it again with the same arguments while it's running returns the same port.
- If the node needs signing in again (no key and no saved identity, or the key was used or has expired),
  `Start` fails and `Status()` says `needs-login`.

C (`FlexarrTunnel.xcframework`, `import FlexarrTunnel` in Swift):

```c
int   FlexarrTunnelStart(char* stateDir, char* hostname, char* authKey, char* target, char* controlURL); // port, or -1
char* FlexarrTunnelLastError(void);   // NULL if the last start worked
char* FlexarrTunnelStatus(void);
void  FlexarrTunnelStop(void);
void  FlexarrTunnelSetVerbose(int on);
void  FlexarrTunnelFree(char* p);     // frees strings returned above
```

The module map links Security and CoreFoundation itself, so the app needs no extra linker settings.

## Build

Needs Go (the version in `go.mod`), Xcode for Apple, and the Android SDK, an NDK and a JDK for Android.

```sh
go test ./...              # two tsnet nodes on Tailscale's in-process test control server
./build-apple.sh           # build/FlexarrTunnel.xcframework (iOS, iOS simulator, tvOS, tvOS simulator; arm64)
./build-android.sh         # build/flexarr-tunnel.aar (arm64-v8a, armeabi-v7a, x86_64; minSdk 26)
```

`tags.sh` lists the tsnet features left out of release builds. gomobile doesn't support tvOS, so
`build-apple.sh` builds each slice as a Go c-archive with `GOOS=ios` and the SDK's clang.

On Android, apps aren't allowed to list network interfaces (netlink), so `netmon_android.go` registers a
fallback that reports the addresses the kernel would use for outbound traffic.

## Spike

`spike/` is a throwaway check that the tunnel runs in an app. `spike/control` runs a test control server,
a DERP relay and a "home" node serving a folder on `127.0.0.1`; `spike/app` is a tvOS and iOS app that
starts the tunnel, fetches `/` and plays a video through it; `spike/probe` does the same from a terminal.

```sh
mkdir -p spike/media && ffmpeg -f lavfi -i testsrc=duration=10:size=1280x720:rate=30 -f lavfi -i sine=duration=10 \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart spike/media/test.mp4 && \
  ffmpeg -i spike/media/test.mp4 -c copy -f hls -hls_time 2 -hls_playlist_type vod spike/media/test.m3u8
go run ./spike/control -dir spike/media            # prints CONTROL_URL, TARGET, AUTHKEY
(cd spike/app && xcodegen)                         # then build TunnelSpikeTV or TunnelSpikeiOS for a simulator
SIMCTL_CHILD_CONTROL_URL=... SIMCTL_CHILD_TARGET=... xcrun simctl launch --console-pty <device> io.flexarr.spike.tv
```
