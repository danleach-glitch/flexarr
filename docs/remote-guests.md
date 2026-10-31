# Family invites: plan

Being built on `upgrade/remote-access` (discussion #146). Lets someone who isn't technical watch your Flexarr from their own home with no Tailscale app, no Tailscale account and no sign-in. You send their phone a one-time link, or type in the code their TV shows. The apps carry Tailscale inside them and join your tailnet as a **Flexarr guest** that can reach Flexarr and nothing else. The server is never on the public internet.

Decided:

- **Server side:** uses the Tailscale already installed on the server (Mac, Linux, Windows). Docker and the Mac app need Tailscale built into the server, which is a later branch.
- **What a guest gets:** one profile, fixed by the invite. A guest can't switch profiles, unlock settings, make TV codes, or see or control the household's devices.
- **Removing a guest:** the server refuses everything from that tailnet device, and links the owner to the device in Tailscale's admin page to delete it.
- **Flexarr's Tailscale access** is an OAuth client tagged `tag:flexarr-guest` with only `auth_keys` and `policy_file:read`. It can create guest keys and read the policy. It can't change the policy or touch other devices.
- **Phone invites:** `https://flexarr.io/join#<invite>`. The invite sits after the `#`, so browsers never send it to flexarr.io.
- **TV codes** go through the push relay. It holds the TV's public key for up to 10 minutes, and the server encrypts the invite to that key.
- **Invites** work once and last 3 days.
- **Hidden** behind `RemoteGuests:Enabled` (off) until the iPhone app that carries the tunnel is on the App Store.

## The owner's setup, once (web Settings › Remote access › Family away from home)

1. **Paste the policy snippet** into the tailnet policy file. Flexarr fills in the server's tailnet address:

   ```jsonc
   "tagOwners": { "tag:flexarr-guest": ["autogroup:admin"] },
   "hosts":     { "flexarr": "100.x.y.z" },
   "grants": [
     { "src": ["tag:flexarr-guest"], "dst": ["flexarr"], "ip": ["tcp:8383"] },
     // Faster relay, when switched on:
     { "src": ["tag:flexarr-guest"], "dst": ["flexarr"], "app": { "tailscale.com/cap/relay": [] } }
   ]
   ```

   The tag must exist before an OAuth client can be made for it, which is why this step comes first.

   Grants allow only what they list. But a policy that still has the default `{"src":["*"],"dst":["*"],"ip":["*"]}` rule lets everything through, `*` matches tagged devices, and the check below refuses it. The setup page explains how to narrow `*` to `autogroup:member`.

2. **Create an OAuth client** (Tailscale admin › Settings › Trust credentials) with the `auth_keys` scope (write, tag `tag:flexarr-guest`) and `policy_file:read`. Paste its ID and secret into Flexarr.

3. Flexarr checks everything live:
   - Tailscale is running.
   - The client works.
   - The policy is safe.
   - The relay is on, if chosen.

## The safety check

Before creating any invite, and every hour, the server:

1. reads the policy (`GET /api/v2/tailnet/-/acl`);
2. adds `tests` to it:
   - a guest is accepted on `flexarr:<port>`;
   - a guest is denied on the server's other ports (22, 445, 5900, 8443) and on port 22, 80 and 443 of each other device on the tailnet;
3. asks Tailscale to validate the result (`POST /api/v2/tailnet/-/acl/validate`, which only needs `policy_file:read`).

A failing test means guests could reach more than Flexarr. Invites are then refused, waiting invites stop being redeemable, and the main profile's phones get one alert. The device list comes from `tailscale status`, so no device scope is needed.

## An invite

The owner picks a profile and a name ("Gran's iPad"). The server:

1. mints a Tailscale auth key: one use, pre-approved, `tag:flexarr-guest`, not ephemeral, expiring with the invite;
2. stores the invite in `invites.json` next to `devices.json`, mode 0600, holding only the SHA-256 of a 32-byte token;
3. builds the payload, which is base64url JSON:

   ```json
   {"v":1,"n":"Dan's Flexarr","s":"<serverId>","a":"100.x.y.z:8383","k":"tskey-auth-…","t":"<token>","g":"Gran's iPad","p":"Gran"}
   ```

The link is `https://flexarr.io/join#<payload>`, and the QR code holds the same link.

The guest's app:

1. checks the payload;
2. asks "Join Dan's Flexarr as Gran?";
3. joins the tailnet with the key (hostname `flexarr-guest-<name>`);
4. calls `POST /api/v1/invites/redeem` with the token over the tunnel.

The server accepts the redeem only from a tailnet address that `tailscale whois` reports as tagged `tag:flexarr-guest`. It records that address and returns a device sign-in locked to the profile. A second redeem gets "already used".

## A TV code

1. The TV makes a P-256 key pair and a random 8-character code, and registers `{code, publicKey}` with the relay (`POST /api/pair`). The relay keeps it for 10 minutes.
2. The owner types the code into Settings. The server fetches the TV's public key (`GET /api/pair/<code>`), creates an invite and seals the payload:
   - ECDH with a fresh P-256 key;
   - HKDF-SHA256 with `info = "flexarr-tv-invite"`;
   - AES-256-GCM.

   It posts `{serverPublicKey, sealed}` back (`POST /api/pair/<code>/invite`).
3. The TV polls (`GET /api/pair/<code>/invite` with its own secret), collects the invite once, opens it and joins as a phone would.

The relay sees public keys and ciphertext only. Whoever runs it could in theory swap the TV's key during those 10 minutes. It's Flexarr's own relay, the code works once, and the owner sees the device join and gets told.

## Guests on the server

A guest sign-in is a device sign-in with `Guest = true`, a fixed `ProfileId` and the device's tailnet address. Compared with other sign-ins, a guest:

- is not trusted: no profile switching, no PIN unlock, no admin rights;
- is left out of `/devices`, remote commands, the device event stream and the household's Now Playing;
- can't make TV pairing codes.

Linking `tagged-devices` to a profile under "People on your tailnet" is refused.

**The gate:** a request from a guest-tagged tailnet address that doesn't carry that device's own live guest sign-in gets 403, whatever it asks for. The redeem endpoint is the one exception. So Remove (revoking the sign-in) blocks the device outright, including pages that need no sign-in.

**Connection check:** `tailscale status --json` says, for each guest device, whether it's direct (`CurAddr`), through the server's peer relay (`PeerRelay`) or through Tailscale's shared relay (`Relay` only). It's shown in Settings. A guest on the shared relay, which slows to about 2 Mbps, gets a bitrate cap.

**Faster relay:** the server runs `tailscale set --relay-server-port=<port>` and the snippet gains the relay grant. Settings says which UDP port to forward.

**Invite used:** the main profile's phones get "Gran's iPad joined as Gran".

## In the apps

**The tunnel** (`clients/tunnel`) is one small Go library on [tsnet](https://pkg.go.dev/tailscale.com/tsnet):

- `Start(stateDir, hostname, authKey, target)` listens on `127.0.0.1:<random>` and forwards each connection into the tailnet to `target`.
- `Status()` gives the path.
- `Stop()` closes it.

Log upload is off. The state lives in a folder that isn't backed up, and the node keeps its identity across launches; the auth key is used only once. It's built as an xcframework (iOS, iOS simulator, tvOS, tvOS simulator) and an AAR (arm64, armv7, x86_64) in CI.

**Guest sign-ins** keep only the tunnel target, never a home address. Otherwise a machine on the guest's own network at the same local address could be sent the sign-in. The app's base address is `http://127.0.0.1:<port>`, so the API, artwork, AVPlayer, AetherEngine and ExoPlayer all go through the tunnel.

What guests lose:

- **AirPlay and Chromecast:** those devices can't reach a tunnel inside the phone, so the buttons are hidden.
- **iPhone downloads:** they run only while the app is open, since the system's background download service can't use the tunnel.
- **Apple TV Top Shelf:** shows nothing for a guest TV.

## Turning it on

Family invites stay hidden (`RemoteGuests:Enabled` false) until the iPhone app that carries the tunnel is on the App
Store. When they're turned on by default:

- add `share-with-family` to `docs/guide/index.json` (after `away-from-home`), link it from `away-from-home.md` and the
  FAQ, and run `make guide`;
- add `ANDROID_CERT_SHA256` to the relay's environment, and Upstash, if they aren't there yet;
- add the changelog entry (Settings › Remote access › Family invites; Join someone’s Flexarr with a code on every app; Paste link);
- answer App Store Connect's export compliance question once (standard encryption algorithms, no documents) and add
  the `ITSEncryptionExportComplianceCode` it gives beside `ITSAppUsesNonExemptEncryption` in `clients/apple/project.yml`.

## Later

- Invite from the iPhone and Android apps.
- Per-guest quality and stream limits.
- Tailscale inside the server, for Docker and the Mac app.
- Moving a guest to a new device.
- Background downloads for guests.
