import { Fragment, lazy, Suspense, useEffect, useState, type ComponentType, type FormEvent, type ReactNode } from 'react'
import { BrowserRouter, Route, Routes, useLocation, useParams } from 'react-router'
import {
  ApiError,
  claimSetup,
  clearApiCache,
  getMe,
  getProfiles,
  getSystemStatus,
  migrateStoredApiKey,
  profileToken,
  saveProfileToken,
  setupStatus,
  signIn,
  UnauthorizedError,
  type InstallKind,
  type Profile,
} from './api/client'
import { ErrorBoundary } from './components/ErrorBoundary'
import { ProfilePicker } from './components/ProfilePicker'
import { Layout } from './components/Layout'
import { AudioPlayerProvider } from './hooks/audioPlayer'
import { LibrariesProvider } from './hooks/libraries'
import { SessionProvider } from './hooks/session'
import { CollectionPage, CollectionsPage } from './pages/CollectionsPage'
import { DiscoverPage } from './pages/DiscoverPage'
import { ExternalTitlePage } from './pages/ExternalTitlePage'
import { HomePage } from './pages/HomePage'
import { ItemPage } from './pages/ItemPage'
import { LibraryPage } from './pages/LibraryPage'
import { MixPage } from './pages/MixPage'
import { PersonPage } from './pages/PersonPage'
import { PlaylistPage, PlaylistsPage } from './pages/PlaylistsPage'
import { PodcastBrowsePage, PodcastEpisodePage, PodcastPage, PodcastsPage } from './pages/PodcastPages'
import { UpcomingPage } from './pages/UpcomingPage'
import { LinkPage } from './pages/LinkPage'
import { errorReporter } from './lib/errorReporter'
import { configureSentry } from './lib/sentry'
import { session } from './lib/session'
import './styles/tokens.css'
import './styles/base.css'
import './styles/app.css'
import './styles/detail.css'
import './styles/player.css'
import './styles/settings.css'
import './styles/podcasts.css'
import './styles/livetv.css'

// The player (with hls.js) and settings load on demand to keep the first page light.
/**
 * Loads a page on demand. A tab opened before an update can ask for a file the update replaced; then the page reloads
 * once to pick up the new version rather than going blank. (Deploys also keep a week of old files.)
 */
/**
 * A page that starts afresh for each title it shows: going from one to another (a Similar title, a person's film)
 * never shows the last one's details, or its Play button, under the new address while the new one loads.
 */
function Fresh({ children }: { children: ReactNode }) {
  const params = useParams()
  return <Fragment key={Object.values(params).join('/')}>{children}</Fragment>
}

function lazyPage<T extends { default: ComponentType }>(load: () => Promise<T>) {
  return lazy(async () => {
    try {
      const page = await load()
      session.remove('flexarr.reloadedForUpdate')
      return page
    } catch (error) {
      if (!session.get('flexarr.reloadedForUpdate')) {
        session.set('flexarr.reloadedForUpdate', '1')
        location.reload()
        return new Promise<T>(() => {}) // the reload replaces this page
      }
      throw error
    }
  })
}

const PlayerPage = lazyPage(() => import('./pages/PlayerPage').then((m) => ({ default: m.PlayerPage })))
const SettingsPage = lazyPage(() => import('./pages/settings/SettingsPage').then((m) => ({ default: m.SettingsPage })))
const LiveTvPage = lazyPage(() => import('./pages/LiveTvPage'))
const LivePlayerPage = lazyPage(() => import('./pages/LivePlayerPage'))

type ConnectionState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  /** `asked`: opened on purpose (Switch profile), so it can be backed out of. */
  | { kind: 'choose-profile'; profiles: Profile[]; asked: boolean; initial?: Profile }
  | { kind: 'unauthorized'; wrongKey?: boolean; setupOpen?: boolean; wrongCode?: boolean; pinMismatch?: boolean }
  | { kind: 'error'; message: string }

/** Everything below the connection check; rendered inside a router so tests can supply a MemoryRouter. */
export function Shell() {
  const { pathname } = useLocation()
  return (
    <SessionProvider>
      <LibrariesProvider>
        <AudioPlayerProvider>
          <ErrorBoundary resetKey={pathname}>
            <Suspense fallback={null}>
              <Routes>
                <Route path="play/:id" element={<PlayerPage />} />
                <Route path="live/:id" element={<LivePlayerPage />} />
                <Route element={<Layout />}>
                  <Route index element={<HomePage />} />
                  <Route path="library/:id" element={<Fresh><LibraryPage /></Fresh>} />
                  <Route path="item/:id" element={<Fresh><ItemPage /></Fresh>} />
                  <Route path="person/:id" element={<Fresh><PersonPage /></Fresh>} />
                  <Route path="collections" element={<CollectionsPage />} />
                  <Route path="collection/:id" element={<Fresh><CollectionPage /></Fresh>} />
                  <Route path="playlists" element={<PlaylistsPage />} />
                  <Route path="playlist/:id" element={<Fresh><PlaylistPage /></Fresh>} />
                  <Route path="mix/:id" element={<Fresh><MixPage /></Fresh>} />
                  <Route path="livetv" element={<LiveTvPage />} />
                  <Route path="podcasts" element={<PodcastsPage />} />
                  <Route path="podcasts/browse" element={<PodcastBrowsePage />} />
                  <Route path="podcasts/episode/:id" element={<Fresh><PodcastEpisodePage /></Fresh>} />
                  <Route path="podcasts/:id" element={<Fresh><PodcastPage /></Fresh>} />
                  <Route path="discover" element={<DiscoverPage />} />
                  <Route path="discover/:mediaType/:tmdbId" element={<Fresh><ExternalTitlePage /></Fresh>} />
                  <Route path="upcoming" element={<UpcomingPage />} />
                  <Route path="link" element={<LinkPage />} />
                  <Route path="settings" element={<SettingsPage />} />
                  <Route path="settings/:section" element={<SettingsPage />} />
                  <Route path="*" element={<p className="page-message muted">There's nothing at this address.</p>} />
                </Route>
              </Routes>
            </Suspense>
          </ErrorBoundary>
        </AudioPlayerProvider>
      </LibrariesProvider>
    </SessionProvider>
  )
}

/**
 * A setup code handed over in the link (`/#setup=ABCD-EFGH`, as the Mac app opens a new server), taken out of the
 * address bar straight away. After the #, so the browser never sends it to the server or keeps it in its logs.
 */
function takeLinkedSetupCode() {
  const code = new URLSearchParams(window.location.hash.slice(1)).get('setup')?.trim()
  if (code) window.history.replaceState(null, '', window.location.pathname + window.location.search)
  return code || undefined
}

/** Where a new server's setup code is, in a sentence, for the way it was installed. */
function whereTheCodeIs(install: InstallKind | undefined) {
  switch (install) {
    case 'MacApp':
      return 'It’s in the Flexarr menu at the top of your screen.'
    case 'MacLaunchAgent':
      return 'It’s in the file setup-code.txt, in the .flexarr/data folder in your home folder.'
    case 'Docker':
      return 'The Flexarr container’s log has it, and a link that fills it in for you.'
    default:
      return 'The server’s log has it, and a link that fills it in for you.'
  }
}

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const [setupInstall, setSetupInstall] = useState<InstallKind>()
  const [linkedCode] = useState(takeLinkedSetupCode)

  useEffect(() => {
    const controller = new AbortController()
    migrateStoredApiKey()
      .then(() => getSystemStatus(controller.signal))
      // Signed in from here: this browser's errors go to the server's log, as the version the server serves.
      // And to Flexarr's developer too, only when the main profile has switched error reports on.
      .then((status) => {
        const version = status.version.split('+')[0]
        errorReporter.enable(version)
        configureSentry(status.errorReports, version)
      })
      // An older server without profiles still works: carry on as the main profile.
      .then(() =>
        Promise.all([
          getProfiles(controller.signal).catch((e: unknown) => (e instanceof UnauthorizedError ? Promise.reject(e) : [])),
          getMe(controller.signal).catch(() => null),
        ]),
      )
      .then(([profiles, me]) => {
        // A stored choice the server no longer honours (the key or the PIN changed, or the profile went) would
        // quietly watch as someone else, often the main profile: forget it and ask again.
        const token = profileToken()
        const tokenFor = token?.slice(0, token.lastIndexOf('.'))
        // Earlier versions kept the main profile's token after its PIN, to manage the server over Tailscale. The PIN
        // now signs the browser in instead, so that token goes, and over Tailscale the picker asks for the PIN again.
        const oldMainToken = me !== null && tokenFor === 'owner'
        if (token && me && (oldMainToken || tokenFor !== me.profileId)) {
          saveProfileToken(null)
          session.remove('flexarr.profileChosen')
          clearApiCache()
        }
        // Ask who's watching when the household has several profiles and this device hasn't chosen one, or when
        // asked to ("Switch profile"). Otherwise, over Tailscale the person is already known from their account.
        const asked = session.get('flexarr.pickProfile') === '1' || (oldMainToken && me?.method === 'tailscale')
        const chosen = profileToken() !== null || session.get('flexarr.profileChosen') === '1' || me?.method === 'tailscale'
        // Asked for one profile in particular (Switch to the main profile, in Settings): straight to its PIN.
        const initial = asked ? profiles.find((p) => p.id === session.get('flexarr.pickProfileFor')) : undefined
        session.remove('flexarr.pickProfileFor')
        setState(profiles.length > 0 && (asked || (profiles.length > 1 && !chosen)) ? { kind: 'choose-profile', profiles, asked, initial } : { kind: 'ready' })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof UnauthorizedError) {
          errorReporter.disable()
          // A new server nobody has signed in to yet: this browser can set it up, no key needed.
          void setupStatus().then(({ open, install }) => {
            if (controller.signal.aborted) return
            setSetupInstall(install)
            setState({ kind: 'unauthorized', setupOpen: open })
          })
        } else setState({ kind: 'error', message: error instanceof Error ? error.message : String(error) })
      })
    return () => controller.abort()
  }, [attempt])

  // A TV's QR code opens /link?code=…; the code is worth showing even before this device signs in.
  const tvCode = window.location.pathname === '/link' ? new URLSearchParams(window.location.search).get('code') : null

  function submitApiKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    setState({ kind: 'loading' })
    signIn(key)
      .then(() => setAttempt((n) => n + 1))
      .catch((error: unknown) =>
        setState(error instanceof UnauthorizedError ? { kind: 'unauthorized', wrongKey: true } : { kind: 'error', message: error instanceof Error ? error.message : String(error) }),
      )
  }

  function setUp(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    // The server's log gives a link with the code in it; pasted whole, the code is taken from it.
    const typed = form.get('setupCode')?.toString().trim()
    const code = typed?.includes('setup=') ? new URLSearchParams(typed.slice(typed.indexOf('#') + 1)).get('setup')?.trim() : typed
    const pin = form.get('setupPin')?.toString() ?? ''
    if (!code) return
    if (pin !== form.get('setupPinAgain')?.toString()) {
      setState({ kind: 'unauthorized', setupOpen: true, pinMismatch: true })
      return
    }
    setState({ kind: 'loading' })
    claimSetup(code, pin)
      .then(() => setAttempt((n) => n + 1))
      .catch((error: unknown) =>
        // A wrong code asks again; anything else (set up meanwhile, say) looks afresh at where the server is.
        error instanceof ApiError && error.status === 400 ? setState({ kind: 'unauthorized', setupOpen: true, wrongCode: true }) : setAttempt((n) => n + 1),
      )
  }

  if (state.kind === 'choose-profile') {
    return (
      <ProfilePicker
        profiles={state.profiles}
        initial={state.initial}
        onChosen={() => {
          session.remove('flexarr.pickProfile')
          session.set('flexarr.profileChosen', '1')
          setState({ kind: 'ready' })
        }}
        // Carry on as before: opening the picker didn't give up the profile chosen here.
        onCancel={
          state.asked
            ? () => {
                session.remove('flexarr.pickProfile')
                session.set('flexarr.profileChosen', '1')
                setState({ kind: 'ready' })
              }
            : undefined
        }
      />
    )
  }

  if (state.kind === 'ready') {
    return (
      <BrowserRouter>
        <Shell />
      </BrowserRouter>
    )
  }

  // Arrived from a phone's or tablet's code: laid out like the app's own code screen, which it sits beside.
  const codePage = state.kind === 'unauthorized' && !state.setupOpen && tvCode !== null
  const thisDevice = /iPad/.test(navigator.userAgent) ? 'iPad' : /iPhone/.test(navigator.userAgent) ? 'iPhone' : 'device'

  return (
    <div className={codePage ? 'connect connect-code' : 'connect'}>
      <p className="wordmark">
        <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
        flexarr
      </p>
      {codePage && <p className="connect-tagline">Your entertainment, on every screen.</p>}
      <section className="panel" aria-label="Server status">
        {state.kind === 'loading' && <p className="muted">Connecting…</p>}
        {state.kind === 'error' && <p className="error">Can't reach the server ({state.message}). Check that Flexarr is running, then reload.</p>}
        {state.kind === 'unauthorized' && state.setupOpen && (
          <form className="setup-welcome api-key-form" onSubmit={setUp}>
            <h1>Welcome to your Flexarr server</h1>
            {/* Opened from the Mac app (or a link with the code): nothing to look for, only a PIN to choose. */}
            {linkedCode && !state.wrongCode ? (
              <input name="setupCode" type="hidden" value={linkedCode} />
            ) : (
              <>
                <label htmlFor="setupCode">Setup code</label>
                <input id="setupCode" name="setupCode" type="text" autoComplete="one-time-code" autoCapitalize="characters" spellCheck={false} placeholder="ABCD-EFGH" required />
                {state.wrongCode && <p className="error">That isn't this server's setup code.</p>}
                <p className="muted small-print setup-code-help">
                  {whereTheCodeIs(setupInstall)}{' '}
                  <a href="https://flexarr.io/guide/first-setup#enter-the-setup-code" target="_blank" rel="noreferrer">
                    Can’t find it?
                  </a>
                </p>
              </>
            )}
            <p className={linkedCode && !state.wrongCode ? 'muted' : 'muted setup-pin-intro'}>
              Choose a PIN to protect the server’s settings. Watching doesn’t need it.
            </p>
            <div className="field-row">
              <label>
                PIN (required)
                <input name="setupPin" type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{4,8}" title="4 to 8 digits" placeholder="4 to 8 digits" required />
              </label>
              <label>
                Confirm PIN
                <input name="setupPinAgain" type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{4,8}" title="4 to 8 digits" required />
              </label>
            </div>
            {state.pinMismatch && <p className="error">The two PINs don’t match. Type the same one in both.</p>}
            <button type="submit" className="button">
              Set up Flexarr
            </button>
          </form>
        )}
        {state.kind === 'unauthorized' && !state.setupOpen && tvCode && (
          // Arrived from a device's code: this browser isn't signed in, and only a signed-in main profile can let a device in.
          <div className="setup-welcome">
            {/* The same steps, words and look as the app's own code screen. */}
            <h1>Connect this {thisDevice}</h1>
            <ol className="setup-steps">
              <li>
                <span>
                  On your computer, open <strong>{window.location.origin}</strong> in a web browser.
                </span>
              </li>
              <li>
                <span>
                  Go to <strong>Settings</strong>, then <strong>Access</strong>, then <strong>Connect a device</strong>.
                </span>
              </li>
              <li>
                <span>Type in this code:</span>
              </li>
            </ol>
            <p className="setup-code">{tvCode}</p>
          </div>
        )}
        {state.kind === 'unauthorized' && !state.setupOpen && !tvCode && (
          <form className="api-key-form" onSubmit={submitApiKey}>
            <label htmlFor="apiKey">API key</label>
            <p className="muted">Ask whoever set up Flexarr for the key, or connect from home.</p>
            <div className="api-key-row">
              <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
              <button type="submit" className="button">
                Connect
              </button>
            </div>
            {state.wrongKey && <p className="error">That isn't the right key.</p>}
            <p className="muted small-print">
              For whoever set it up: the key is in config.json in the server’s data folder. Opening Flexarr through Tailscale signs you in without it.
            </p>
          </form>
        )}
      </section>
    </div>
  )
}
