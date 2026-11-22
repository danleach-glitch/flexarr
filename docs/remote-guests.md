# Family invites: plan

Being built on `upgrade/remote-access` (discussion #146). Lets someone who isn't technical watch your Flexarr from their own home with no Tailscale app, no Tailscale account and no sign-in. You send their phone a one-time link, or type in the code their TV shows. The apps carry Tailscale inside them and join your tailnet as a **Flexarr guest** that can reach Flexarr and nothing else. The server is never on the public internet.

Decided:

- **Server side:** uses the Tailscale already installed on the server (Mac, Linux, Windows). Docker and the Mac app need Tailscale built into the server, which is a later branch.
- **What a guest gets:** one profile, fixed by the invite. A guest can't switch profiles, unlock settings, make TV codes, or see or control the household's devices.
- **Removing a guest:** the server refuses everything from that tailnet device, and links the owner to the device in Tailscale's admin page to delete it.
- **Flexarr's Tailscale access** is an OAuth client tagged `tag:flexarr-guest` with only `auth_keys` and `policy_file:read` (plus the `devices:core:read` and `devices:posture_attributes:read` Tailscale adds to any client that reads the policy). It can create guest keys and read the policy. It can't change the policy or touch other devices.
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

2. **Create an OAuth client** (Tailscale admin › Settings › Trust credentials) with the `auth_keys` scope (write, tag `tag:flexarr-guest`) and `policy_file:read` (Tailscale adds the two device read scopes itself). Paste its ID and secret into Flexarr.

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

1. The TV makes a P-256 key pair and an 8-character code (random in TV apps before the code proves the key; made from the key since, below), and registers `{code, publicKey}` with the relay (`POST /api/pair`). The relay keeps it for 10 minutes.
2. The owner types the code into Settings. The server fetches the TV's public key (`GET /api/pair/<code>`), creates an invite and seals the payload:
   - ECDH with a fresh P-256 key;
   - HKDF-SHA256 with `info = "flexarr-tv-invite"`;
   - AES-256-GCM.

   It posts `{serverPublicKey, sealed}` back (`POST /api/pair/<code>/invite`).
3. The TV polls (`GET /api/pair?code=<code>` with its own secret in an `X-Pair-Secret` header, kept out of request logs; TV apps before that put it in the query), collects the invite once, opens it and joins as a phone would.

The relay sees public keys and ciphertext only. With a random code, whoever runs it could in theory swap the TV's key during those 10 minutes. The code made from the key, below, closes that.

### The code proves the key

A TV app makes its code from its public key, so the server can tell the key the relay hands back is the TV's:

- `h` = SHA-256 of the UTF-8 bytes of `publicKey`, the base64 string exactly as the TV sends it to the relay.
- Characters 1 to 7: `ALPHABET[h[i] % 31]` for `i` = 0 to 6, where `ALPHABET` = `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (`GuestTvCodes.Alphabet`, `relay/api/pair.js`).
- Character 8, the check: `ALPHABET[(sum of (i + 1) × index(cᵢ) for i = 0 to 6) % 31]`.

When the code typed ends in its check character, the server refuses a key that doesn't make it ("That code doesn’t match the device showing it"), and the relay won't keep a code under a key that doesn't make it (409 `CodeTaken`, so an older TV app whose random code ends in its check character by chance just makes another). A code that fails the check comes from a TV app before this and is taken as before. If the code is already taken, the TV makes a new key.

Test vectors (`relay/test/pair.test.js` and `RemoteGuestTests` check the same ones):

| publicKey | code |
|---|---|
| `BAabxf2q9hMu/dxG+nGp5nYWsRDth260mBqx6U/8TU9miOPz86o/f4xuMSzrZ/YVxS1EHcdBY/JxFPKujcWATRA=` | `P5GSPMQP` |
| `BJeO6XNfhIDBbIdQxtPpYlj3Cjk7nlUzjg+xJFmk20ChvzfI1jdWs2T5CgsWu1MmBW4iXHxrMfK9/MTDb9Nb1mM=` | `VFAZC453` |
| `BBikfwYmLGipShyfrqxMWA/kv+NWBDrgrZotgMESONlNQfZ0um1pjfF8J+1iarUDMOzmd5+F9czYFE5xxV+pIvo=` | `7EAR565J` |

## Guests on the server

A guest sign-in is a device sign-in with `Guest = true`, a fixed `ProfileId` and the device's tailnet address. Compared with other sign-ins, a guest:

- is not trusted: no profile switching, no PIN unlock, no admin rights;
- is left out of `/devices`, remote commands, the device event stream and the household's Now Playing;
- can't make TV pairing codes.

Linking `tagged-devices` to a profile under "People on your tailnet" is refused.

**The gate:** a request from a guest-tagged tailnet address that doesn't carry that device's own live guest sign-in gets 403, whatever it asks for. The redeem endpoint is the one exception. So Remove (revoking the sign-in) blocks the device outright, including pages that need no sign-in. If `tailscale whois` can't answer (it timed out, or tailscaled is restarting) and a guest has ever redeemed an invite from that address, the request gets 503 with `Retry-After` rather than being let in; any other tailnet device carries on. Pairing refuses a device whois can't identify. The gate does nothing on a server that has never had family invites set up or a guest.

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

## Household devices away from home

Someone who lives in the house (Claire) shouldn't have to be a guest to watch from her iPad away from home, or install
Tailscale. The owner adds her device in **Settings › Remote access › Your household**: the same code on her device, the
same built-in tunnel and tag, but the device signs in to **her own household profile**, with none of a guest's limits.

- **The invite** carries `Household = true` (stored server-side in invites.json; the payload is unchanged, so every app
  that can join a guest can join this). Creating one never writes guests.json, so the profile never shows under Guests.
  It's refused for the main profile (that keeps using Tailscale) and for a guest's profile.
- **The sign-in** is an *away* sign-in (`SignIn.Away`): a household device for its profile, trusted like one at home
  (switch to other household profiles, see and control the household's TVs and phones, Bazarr, music requests, DVR), but
  with a guest's safety: valid only from the tailnet address that redeemed it, its token under the `guest:` prefix (a
  server rolled back to before household devices finds nothing, rather than a guest), and never the main profile: it
  can't be chosen there (nor a guest's) and the device never unlocks the server's settings. It keeps the profile it
  was added for as `HomeProfileId`: if the profile it switched to goes, it goes back there; if that one goes, it's
  signed out. A kids profile's device comes with child lock on.
  `SignIn.Tunneled` (guest or away) is what the gate and the address binding check; `Guest` is what the limits check.
- **The API** only grows: `household` on `CreateInviteRequest`/`TvCodeRequest` (in), and on `GuestInviteDto`,
  `GuestDeviceDto` and `RedeemedInviteDto` (out). `/me`'s `isGuest` keeps its meaning and is false for these devices.
- **Removing** a household device (the bin, `DELETE /remote/guests/devices/{id}`) only signs it out. There's no remove
  person for them: the profile is the household's.
- **The apps** keep "uses the tunnel" (AirPlay, background downloads, Top Shelf, no remote address) apart from "is a guest"
  (profile lock, kids lock, Leave, remote commands), reading `household` from the redeem answer and `isGuest` from `/me`.
  An app from before treats the device as a guest: it works, with a guest's limits in the app, until it's updated.
- **Tailscale:** the device is tagged `tag:flexarr-guest` like a guest's, so the policy keeps it to Flexarr's port, and
  pausing invites for an unsafe policy pauses household joins too.
- **Guest or household, not both:** a guest can't be invited to the profile of someone with a household device (or one
  waiting), and redeeming checks again. A guest who turns out to live in the house moves across with
  `POST /remote/guests/people/{profileId}/move-to-household`: their devices become away ones and their waiting invites
  household ones, keeping the profile.
- **Rolling back to 0.66** with household invites still waiting: 0.66 doesn't know `Household`, so it reads them as
  guests' invites, and a device that redeems one there joins as a guest of that profile. Cancel waiting household invites
  before going back. Devices already joined are safe: 0.66 finds nothing for their tokens, so they're signed out rather
  than becoming guests.

## Household devices at home

A household device that joined by invite shouldn't need signing out when it comes home. It uses the server's home
address whenever that answers and goes through the tunnel when it doesn't, the way a device signed in at home already
switches between its home and away addresses. Family guests are unchanged: tunnel only, never told the home address.

- **The key works at home too.** An away sign-in is accepted from the tailnet address that redeemed it (as before) or
  from the home network: zone `Local`, not loopback, addressed to one of the server's names, and not handed over by a
  container's gateway (the rules the home network signing in already uses). Never from the internet or another tailnet
  address. A guest's sign-in stays bound to its tailnet address. The trade-off: a copied household key works on the
  home Wi-Fi, which is what any home device's key already does.
- **The server says where home is.** `/me` gains `homeUrl` (optional) for away sign-ins only: `http://<LAN IPv4>:<port>`,
  the private address of a running interface with a gateway (Ethernet and Wi-Fi first, VPN and virtual machine
  adapters last) and the port Kestrel listens on for plain HTTP. `Flexarr:HomeUrl` sets it by hand. In Docker without
  that it's null (the container's own address isn't the host's), so a Docker server's household devices stay on the
  tunnel, and the key isn't accepted from home either (port forwarding can make anyone look local). Any trouble
  reading the interfaces also means null, never a failed `/me`. A device already joined learns it the next time it
  asks `/me`, so nobody joins again.
- **The app checks before it sends the key.** A household sign-in keeps `homeAddress` and the server's id (never for a
  guest). The home address only gets the key once it proves it's the same server (`/setup` with a challenge, as home
  sign-ins do), so a lookalike address on someone else's network gets nothing. The server won't answer that proof for
  a guest's or household device's key asked from the internet, so it can't be passed on from a server that's also
  public. The tunnel headers never go to it.
- **Switching is the existing race.** `ServerAddresses` gets home as the home address and the tunnel's loopback
  address as the away one, so requests go to whichever answered last, GETs race the other after a moment, and home is
  tried again every few minutes while away. The last route that answered is kept, so a launch at home goes straight
  home. The apps only try home when the server sent `homeUrl`: an older server refuses the key at home, and that
  refusal must never look like a sign-out.
- **The tunnel at home** stops (identity kept: `stop()` without forget) once home has answered, and starts again from
  its saved identity when home stops answering. A download that loses the home address carries on through the tunnel.
- **What follows the route.** AirPlay, Chromecast, background downloads, Top Shelf (only a proven home address) and
  Android TV's Watch Next work while the device is home and go away when it isn't; a player already open keeps the
  route it opened on. What follows the sign-in stays: Leave/Sign out, removal, the profile rules, downloads' server key
  (the server's id, so a download made at home matches away).
- **The owner sees it.** Each away sign-in remembers whether its last request came from home or through the tunnel,
  shown as `lastRoute` (`home`/`away`, optional) on `GuestDeviceDto` and in Settings › Remote access › Your household.
- **Settings** in the apps say *At home* or *Away from home* for the route in use.

## Turning it on

Family invites are on by default (they were hidden until the apps carrying the tunnel were in the stores):

- the server: `RemoteGuests.Enabled` is true in `FlexarrOptions` (`Flexarr:RemoteGuests:Enabled=false` hides them);
- the Apple apps: `FLEXARR_GUEST_INVITES` is `YES` in `clients/apple/project.yml` (`NO` hides Join in a release
  build; debug builds and `-FlexarrGuestInvites YES` always have it);
- Android: `flexarr.guestInvites` is on in `clients/android/app/build.gradle.kts` (`-Pflexarr.guestInvites=false`
  hides it). The same switch packages the tunnel: a release build without it leaves the Go library (`libgojni.so`,
  about 18 MB on each device) out and can't start a guest's tunnel;
- the relay has its store (`KV_REST_API_URL`/`KV_REST_API_TOKEN`) and `ANDROID_CERT_SHA256`;
- export compliance is settled: the apps declare exempt encryption (`ITSAppUsesNonExemptEncryption` false, the reasoning
  beside it in `clients/apple/project.yml`).

## Later

- Invite from the iPhone and Android apps.
- Per-guest quality and stream limits.
- Tailscale inside the server, for Docker and the Mac app.
- Moving a guest to a new device.
- Background downloads for guests.
