# Securing server settings: plan

> Since 0.57 the admin PIN is apart from the main profile's own PIN, which is optional and only guards watching as it; see `docs/architecture.md` (Profiles). Below, "the main profile's PIN" means the admin PIN.

Being built on `feat/secure-server-settings`. Today, managing the server comes with the main profile. A household that never gave the main profile a PIN lets anyone at home, or anyone with a paired TV, open Settings, approve new devices, delete media or read the API key. Tailscale guests are already kept out.

Decided so far:

- **Watching and managing are separate.** Choosing the main profile still lets you watch as it. Managing the server takes the main profile's PIN as well, and that lasts for a while on that browser or device. Older apps aren't a concern: none has shipped to anyone else yet.
- **The PIN is compulsory.** Setup asks for it. A server that has none yet (set up earlier, or signed in with the API key) asks for one before anything else in Settings.
- **The home network doesn't skip it.** Being at home is exactly who this guards against.
- **Forgetting it:** the API key (in `config.json`, which only whoever has the server can read) sets a new PIN. The Mac app does it from its menu.

## What people get

- **Setup** asks for the setup code and the main profile's PIN together. Closing the page before pressing the button leaves the server as it was, setup still open with the same code; afterwards, the PIN is set.
- **Unlocking:** opening Settings (or anything else that manages the server) on the main profile asks for its PIN. It stays unlocked on that browser or device for 15 minutes after the last change, until someone else is chosen there, or until **Lock** is pressed. Restarting the server locks everything.
- **Choosing the main profile** no longer needs the PIN on a browser or device the owner signed in, unless the main profile has a PIN of its own (since 0.57; before, the **Ask for the PIN when choosing the main profile** switch). A Tailscale guest still needs the admin PIN.
- **Approving a new device** shows on the main profile as before; approving it asks for the PIN if it isn't unlocked.
- **Forgotten it?** sits wherever the PIN is asked for. In a browser it takes the API key and goes straight to setting a new PIN (the main profile's PIN can no longer be removed). The TV and phone apps say to reset it from a browser. The Mac app's menu has **Reset Admin PIN…**, which opens the web app at "Set a new PIN" without the key: being at the Mac is proof enough.
- **Guessing:** the unlock uses the same guard as every other PIN (lockouts doubling to an hour per guesser, the profile throttled to one try every five seconds). A locked main profile doesn't count as already managing the server, so it gets the stricter count. When guesses at the main profile's PIN lock someone out, the server logs it and tells the owner (a push notification and the event stream).

## How it works

- **The Admin claim** needs the main profile and an unlock. The API key is always admin; it is the server. Unlocks are kept in memory per sign-in (browser or device), with when they lapse.
- **Callers without a sign-in** (Tailscale, the home-network rule) get a browser sign-in when they unlock, as choosing the main profile over Tailscale does now, and the unlock is kept on it.
- **`/me`** gains `canUnlock` (the main profile is chosen but not unlocked), `adminPinMissing` (the main profile has no PIN yet) and `unlockedUntil`.
- **Endpoints:** `POST /admin/unlock` (`pin`, or `apiKey`), `POST /admin/lock`, `POST /admin/pin` (sets the admin PIN: the first one, by a trusted main-profile caller; a new one with `currentPin` (0.57); or a new one with the API key, which is the recovery path), `POST /setup/claim` takes `pin`.
- **Tidying the library isn't managing the server.** Delete, Fix match, Allow/Block for kids, Analyse, Scan for changes and the household's collections take the main profile (`MainProfilePolicy`: chosen, locked or not), not the unlock. A main profile that's chosen without its PIN can use them, as it could before the lock.
- **Lapsing:** an admin-only request that changes something (not a GET) pushes the 15 minutes on. Playback and other everyday requests don't.

## Order

1. Server: claim with PIN, unlocks, the Admin claim, endpoints, `/me`, the guard and the lockout notice. Tests.
2. Web: setup, the unlock prompt and Lock, Settings › Profiles (no removing the main PIN, the switching option, recovery), the pairing prompt.
3. Mac app: Reset Admin PIN.
4. Apple and Android apps: the unlock prompt where they manage the server; "reset it from a browser".
5. Guide (`docs/guide/profiles.md`, troubleshooting) and `docs/architecture.md`.
