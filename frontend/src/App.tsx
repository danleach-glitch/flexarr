import { Fragment, lazy, Suspense, useEffect, useState, useSyncExternalStore, type ComponentType, type FormEvent, type ReactNode } from 'react'
import { BrowserRouter, Navigate, Outlet, Route, Routes, useLocation, useParams } from 'react-router'
import {
  ApiError,
  claimSetup,
  clearApiCache,
  getMe,
  getProfiles,
  getSystemStatus,
  migrateStoredApiKey,
  PICK_PROFILE_EVENT,
  profileToken,
  saveProfileToken,
  setupStatus,
  SIGNED_OUT_EVENT,
  signIn,
  UnauthorizedError,
  type InstallKind,
  type Profile,
} from './api/client'
import { ErrorBoundary } from './components/ErrorBoundary'
import { beginLanguage, currentLanguage, onLanguageChange, t, Words } from './i18n'
import { ProfilePicker } from './components/ProfilePicker'
import { Layout } from './components/Layout'
import { AudioPlayerProvider } from './hooks/audioPlayer'
import { LibrariesProvider } from './hooks/libraries'
import { SessionProvider } from './hooks/session'
import { useAgeLimits, useSession } from './hooks/sessionContext'
import { HomePage } from './pages/HomePage'
import { ItemPage } from './pages/ItemPage'
import { LibraryPage } from './pages/LibraryPage'
import { PersonPage } from './pages/PersonPage'
import { errorReporter } from './lib/errorReporter'
import { onDemand } from './lib/onDemand'
import { deviceCode } from './lib/pairing'
import { configureSentry } from './lib/sentry'
import { forgetPlayed, startKeepingPlayed } from './lib/profileScope'
import { session } from './lib/session'
import './styles/tokens.generated.css'
import './styles/tokens.css'
import './styles/base.css'
import './styles/app.css'
import './styles/detail.css'
import './styles/player.css'
import './styles/settings.css'
import './styles/podcasts.css'
import './styles/livetv.css'
import './styles/books.css'
import { setServerTitle } from './lib/serverTitle'
import { errorText } from './lib/errorText'

// The player (with hls.js) and settings load on demand to keep the first page light.
/**
 * A page that starts afresh for each title it shows: going from one to another (a Similar title, a person's film)
 * never shows the last one's details, or its Play button, under the new address while the new one loads.
 */
function Fresh({ children }: { children: ReactNode }) {
  const params = useParams()
  return <Fragment key={Object.values(params).join('/')}>{children}</Fragment>
}

/** A page loaded on demand (see onDemand for a tab opened before an update). */
function lazyPage<T extends { default: ComponentType }>(load: () => Promise<T>) {
  return lazy(() => onDemand(load))
}

const PlayerPage = lazyPage(() => import('./pages/PlayerPage').then((m) => ({ default: m.PlayerPage })))
const SettingsPage = lazyPage(() => import('./pages/settings/SettingsPage').then((m) => ({ default: m.SettingsPage })))
const SetupGuidePage = lazyPage(() => import('./pages/SetupGuidePage').then((m) => ({ default: m.SetupGuidePage })))
const LiveTvPage = lazyPage(() => import('./pages/LiveTvPage'))
const LivePlayerPage = lazyPage(() => import('./pages/LivePlayerPage'))
// Pages fewer people open on the way in load on demand too: the first page needs only Home, a library and a title.
const CollectionsPage = lazyPage(() => import('./pages/CollectionsPage').then((m) => ({ default: m.CollectionsPage })))
const CollectionPage = lazyPage(() => import('./pages/CollectionsPage').then((m) => ({ default: m.CollectionPage })))
const DiscoverPage = lazyPage(() => import('./pages/DiscoverPage').then((m) => ({ default: m.DiscoverPage })))
const ExternalTitlePage = lazyPage(() => import('./pages/ExternalTitlePage').then((m) => ({ default: m.ExternalTitlePage })))
const MusicExternalAlbumPage = lazyPage(() => import('./pages/MusicExternalPages').then((m) => ({ default: m.MusicExternalAlbumPage })))
const MusicExternalArtistPage = lazyPage(() => import('./pages/MusicExternalPages').then((m) => ({ default: m.MusicExternalArtistPage })))
const MixPage = lazyPage(() => import('./pages/MixPage').then((m) => ({ default: m.MixPage })))
const LinkPage = lazyPage(() => import('./pages/LinkPage').then((m) => ({ default: m.LinkPage })))
const BookSeriesPage = lazyPage(() => import('./pages/BookSeriesPage').then((m) => ({ default: m.BookSeriesPage })))
const PlaylistsPage = lazyPage(() => import('./pages/PlaylistsPage').then((m) => ({ default: m.PlaylistsPage })))
const PlaylistPage = lazyPage(() => import('./pages/PlaylistsPage').then((m) => ({ default: m.PlaylistPage })))
const PodcastsPage = lazyPage(() => import('./pages/PodcastPages').then((m) => ({ default: m.PodcastsPage })))
const PodcastPage = lazyPage(() => import('./pages/PodcastPages').then((m) => ({ default: m.PodcastPage })))
const PodcastBrowsePage = lazyPage(() => import('./pages/PodcastPages').then((m) => ({ default: m.PodcastBrowsePage })))
const PodcastEpisodePage = lazyPage(() => import('./pages/PodcastPages').then((m) => ({ default: m.PodcastEpisodePage })))
const UpcomingPage = lazyPage(() => import('./pages/UpcomingPage').then((m) => ({ default: m.UpcomingPage })))
const WatchlistPage = lazyPage(() => import('./pages/WatchlistPage').then((m) => ({ default: m.WatchlistPage })))

type ConnectionState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  /** `asked`: opened on purpose (Switch profile), so it can be backed out of. */
  | { kind: 'choose-profile'; profiles: Profile[]; asked: boolean; initial?: Profile }
  | { kind: 'unauthorized'; wrongKey?: boolean; setupOpen?: boolean; wrongCode?: boolean; pinMismatch?: boolean; badPin?: string }
  | { kind: 'error'; message: string }

/**
 * Pages a child's profile doesn't have (podcasts, the watchlist, collections, Live TV): a link to one goes Home. Nothing
 * shows (or is asked for) until it's known who's watching.
 */
function NotForKids() {
  const { kids } = useAgeLimits()
  const { meLoaded } = useSession()
  if (!meLoaded) return null
  return kids ? <Navigate to="/" replace /> : <Outlet />
}

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
                <Route element={<NotForKids />}>
                  <Route path="live/:id" element={<LivePlayerPage />} />
                </Route>
                <Route path="setup" element={<SetupGuidePage />} />
                <Route element={<Layout />}>
                  <Route index element={<HomePage />} />
                  <Route path="library/:id" element={<Fresh><LibraryPage /></Fresh>} />
                  <Route path="item/:id" element={<Fresh><ItemPage /></Fresh>} />
                  <Route path="person/:id" element={<Fresh><PersonPage /></Fresh>} />
                  <Route path="playlists" element={<PlaylistsPage />} />
                  <Route path="playlist/:id" element={<Fresh><PlaylistPage /></Fresh>} />
                  <Route path="mix/:id" element={<Fresh><MixPage /></Fresh>} />
                  <Route element={<NotForKids />}>
                    <Route path="collections" element={<CollectionsPage />} />
                    <Route path="collection/:id" element={<Fresh><CollectionPage /></Fresh>} />
                    <Route path="book-series/:id" element={<Fresh><BookSeriesPage /></Fresh>} />
                    <Route path="livetv" element={<LiveTvPage />} />
                    <Route path="podcasts" element={<PodcastsPage />} />
                    <Route path="podcasts/browse" element={<PodcastBrowsePage />} />
                    <Route path="podcasts/episode/:id" element={<Fresh><PodcastEpisodePage /></Fresh>} />
                    <Route path="podcasts/:id" element={<Fresh><PodcastPage /></Fresh>} />
                    <Route path="watchlist" element={<WatchlistPage />} />
                  </Route>
                  <Route path="discover" element={<DiscoverPage />} />
                  <Route path="discover/:mediaType/:tmdbId" element={<Fresh><ExternalTitlePage /></Fresh>} />
                  <Route path="discover/music/artist/:id" element={<Fresh><MusicExternalArtistPage /></Fresh>} />
                  <Route path="discover/music/album/:id" element={<Fresh><MusicExternalAlbumPage /></Fresh>} />
                  <Route path="upcoming" element={<UpcomingPage />} />
                  <Route path="link" element={<LinkPage />} />
                  <Route path="settings" element={<SettingsPage />} />
                  <Route path="settings/:section" element={<SettingsPage />} />
                  <Route path="*" element={<p className="page-message muted">{t('There’s nothing at this address.')}</p>} />
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
      return t('It’s in the Flexarr menu at the top of your screen.')
    case 'MacLaunchAgent':
      return t('It’s in the file setup-code.txt, in the .flexarr/data folder in your home folder.')
    case 'Docker':
      return t('The Flexarr container’s log has it, and a link that fills it in for you.')
    default:
      return t('The server’s log has it, and a link that fills it in for you.')
  }
}

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: 'loading' })
  // A new language redraws everything in it, and nothing cached from the server is in the old one any more.
  const language = useSyncExternalStore(onLanguageChange, () => currentLanguage().code)
  useEffect(() => {
    clearApiCache()
  }, [language])
  const [attempt, setAttempt] = useState(0)
  // The last page in this tab signed out: nothing it left on its way out is here for this one, however it's signed in.
  useEffect(() => startKeepingPlayed(), [])
  // Unlocking or locking to kids profiles asks who's watching straight away (see pickProfileWith).
  useEffect(() => {
    const again = () => setAttempt((n) => n + 1)
    window.addEventListener(PICK_PROFILE_EVENT, again)
    return () => window.removeEventListener(PICK_PROFILE_EVENT, again)
  }, [])
  // Signed out while using it (the key changed, "Sign out every other browser", an expired cookie): every request
  // fails from then on, so look again, which finds the sign-in form.
  const ready = state.kind === 'ready'
  useEffect(() => {
    if (!ready) return
    const out = () => {
      setState({ kind: 'loading' })
      setAttempt((n) => n + 1)
    }
    window.addEventListener(SIGNED_OUT_EVENT, out)
    return () => window.removeEventListener(SIGNED_OUT_EVENT, out)
  }, [ready])
  const [setupInstall, setSetupInstall] = useState<InstallKind>()
  // Once the linked code turns out wrong (a new one since), the code box stays, whatever happens next.
  const [linkedCode, setLinkedCode] = useState(takeLinkedSetupCode)

  useEffect(() => {
    const controller = new AbortController()
    // Everything the first screen waits for is asked for together, not one after the other. The languages need no
    // sign-in, so they go first of all.
    const language = beginLanguage(controller.signal)
    migrateStoredApiKey()
      .then(() =>
        Promise.all([
          getSystemStatus(controller.signal),
          // An older server without profiles still works: carry on as the main profile.
          getProfiles(controller.signal).catch((e: unknown) => (e instanceof UnauthorizedError ? Promise.reject(e) : [])),
          getMe(controller.signal).catch(() => null),
        ]),
      )
      // Signed in from here: this browser's errors go to the server's log, as the version the server serves.
      // And to Flexarr's developer too, only when the main profile has switched error reports on.
      .then(([status, profiles, me]) => {
        setServerTitle(status.serverName)
        const version = status.version.split('+')[0]
        errorReporter.enable(version)
        configureSentry(status.errorReports, version)
        return [profiles, me] as const
      })
      // The profile's language (or the browser's) before the first real screen, so it doesn't change under you; a
      // server slow to say which doesn't hold the screen up for long.
      .then(async ([profiles, me]) => {
        await language(me?.language ?? null)
        return [profiles, me] as const
      })
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
        // The server answers as nobody chosen until one is (the main profile's own PIN wasn't given here): always ask,
        // with no way back to what was showing.
        const mustChoose = me?.chooseProfile === true
        setState(
          profiles.length > 0 && (mustChoose || asked || (profiles.length > 1 && !chosen))
            ? { kind: 'choose-profile', profiles, asked: asked && !mustChoose, initial }
            : { kind: 'ready' },
        )
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof UnauthorizedError) {
          errorReporter.disable()
          // Not signed in (signed out here or from elsewhere, or the key changed): whoever signs in next finds nothing
          // of what was played before. The page a sign-out leaves may write its queue once more as it goes; this is after.
          forgetPlayed()
          void language(null)
          // A new server nobody has signed in to yet: this browser can set it up, no key needed.
          void setupStatus().then(({ open, install }) => {
            if (controller.signal.aborted) return
            setSetupInstall(install)
            setState({ kind: 'unauthorized', setupOpen: open })
          })
        } else setState({ kind: 'error', message: errorText(error) })
      })
    return () => controller.abort()
  }, [attempt])

  // A TV's QR code opens /link?code=…; the code is worth showing even before this device signs in. Only a code,
  // though: anything else in its place would be shown large, in Flexarr's name, to whoever was sent the link.
  const tvCode = window.location.pathname === '/link' ? deviceCode(new URLSearchParams(window.location.search).get('code')) : null

  function submitApiKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    setState({ kind: 'loading' })
    signIn(key)
      .then(() => setAttempt((n) => n + 1))
      .catch((error: unknown) =>
        setState(error instanceof UnauthorizedError ? { kind: 'unauthorized', wrongKey: true } : { kind: 'error', message: errorText(error) }),
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
      .catch((error: unknown) => {
        // A wrong code or PIN asks again; anything else (set up meanwhile, say) looks afresh at where the server is.
        if (!(error instanceof ApiError) || error.status !== 400) return setAttempt((n) => n + 1)
        const pinProblem = (error.problem?.errors as Record<string, string[]> | undefined)?.pin?.[0]
        if (pinProblem) return setState({ kind: 'unauthorized', setupOpen: true, badPin: pinProblem })
        setLinkedCode(undefined)
        setState({ kind: 'unauthorized', setupOpen: true, wrongCode: true })
      })
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
        <Shell key={language} />
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
      {codePage && <p className="connect-tagline">{t('Your entertainment, on every screen.')}</p>}
      <section className="panel" aria-label={t('Server status')}>
        {state.kind === 'loading' && <p className="muted">{t('Connecting…')}</p>}
        {state.kind === 'error' && <p className="error">{t('Can’t reach the server ({message}). Check that Flexarr is running, then reload.', { message: state.message })}</p>}
        {state.kind === 'unauthorized' && state.setupOpen && (
          <form className="setup-welcome api-key-form" onSubmit={setUp}>
            <h1>{t('Welcome to your Flexarr server')}</h1>
            {/* Opened from the Mac app (or a link with the code): nothing to look for, only a PIN to choose. */}
            {linkedCode ? (
              <input name="setupCode" type="hidden" value={linkedCode} />
            ) : (
              <>
                <label htmlFor="setupCode">{t('Setup code')}</label>
                <input id="setupCode" name="setupCode" type="text" autoComplete="one-time-code" autoCapitalize="characters" spellCheck={false} placeholder="ABCD-EFGH" required />
                {state.wrongCode && <p className="error">{t('That isn’t this server’s setup code.')}</p>}
                <p className="muted small-print setup-code-help">
                  {whereTheCodeIs(setupInstall)}{' '}
                  <a href="https://flexarr.io/guide/first-setup#enter-the-setup-code" target="_blank" rel="noreferrer">
                    {t('Can’t find it?')}
                  </a>
                </p>
              </>
            )}
            <p className={linkedCode ? 'muted' : 'muted setup-pin-intro'}>
              {t('Choose a PIN to protect the server’s settings. Watching doesn’t need it.')}
            </p>
            <div className="field-row">
              <label>
                {t('PIN (required)')}
                <input name="setupPin" type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{4,8}" title={t('4 to 8 digits')} placeholder={t('4 to 8 digits')} required />
              </label>
              <label>
                {t('Confirm PIN')}
                <input name="setupPinAgain" type="password" inputMode="numeric" autoComplete="new-password" pattern="[0-9]{4,8}" title={t('4 to 8 digits')} required />
              </label>
            </div>
            {state.pinMismatch && <p className="error">{t('The two PINs don’t match. Type the same one in both.')}</p>}
            {state.badPin && <p className="error">{state.badPin}</p>}
            <button type="submit" className="button">
              {t('Set up Flexarr')}
            </button>
          </form>
        )}
        {state.kind === 'unauthorized' && !state.setupOpen && tvCode && (
          // Arrived from a device's code: this browser isn't signed in, and only a signed-in main profile can let a device in.
          <div className="setup-welcome">
            {/* The same steps, words and look as the app's own code screen. */}
            <h1>{t('{device, select, iPad {Connect this iPad} iPhone {Connect this iPhone} other {Connect this device}}', { device: thisDevice })}</h1>
            <ol className="setup-steps">
              <li>
                <span>
                  {t.rich('On your computer, open {address} in a web browser.', { address: <strong>{window.location.origin}</strong> })}
                </span>
              </li>
              <li>
                <span>
                  {t.rich('Go to {settings}, then {security}, then {connect}.', {
                    settings: <strong>{t(Words.settings)}</strong>,
                    security: <strong>{t('Security')}</strong>,
                    connect: <strong>{t('Connect a device')}</strong>,
                  })}
                </span>
              </li>
              <li>
                <span>{t('Type in this code:')}</span>
              </li>
            </ol>
            <p className="setup-code">{tvCode}</p>
          </div>
        )}
        {state.kind === 'unauthorized' && !state.setupOpen && !tvCode && (
          <form className="api-key-form" onSubmit={submitApiKey}>
            <label htmlFor="apiKey">{t('API key')}</label>
            <p className="muted">{t('Ask whoever set up Flexarr for the key, or connect from home.')}</p>
            <div className="api-key-row">
              <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
              <button type="submit" className="button">
                {t('Connect')}
              </button>
            </div>
            {state.wrongKey && <p className="error">{t('That isn’t the right key.')}</p>}
            <p className="muted small-print">
              {t('For whoever set it up: the key is in config.json in the server’s data folder. Opening Flexarr through Tailscale signs you in without it.')}
            </p>
          </form>
        )}
      </section>
    </div>
  )
}
