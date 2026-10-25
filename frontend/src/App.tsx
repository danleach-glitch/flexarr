import { lazy, Suspense, useEffect, useState, type ComponentType, type FormEvent } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router'
import {
  clearApiCache,
  getMe,
  getProfiles,
  getSystemStatus,
  migrateStoredApiKey,
  profileToken,
  saveProfileToken,
  signIn,
  UnauthorizedError,
  type Profile,
} from './api/client'
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
import { UpcomingPage } from './pages/UpcomingPage'
import { LinkPage } from './pages/LinkPage'
import './styles/tokens.css'
import './styles/base.css'
import './styles/app.css'
import './styles/detail.css'
import './styles/player.css'
import './styles/settings.css'

// The player (with hls.js) and settings load on demand to keep the first page light.
/**
 * Loads a page on demand. A tab opened before an update can ask for a file the update replaced; then the page reloads
 * once to pick up the new version rather than going blank. (Deploys also keep a week of old files.)
 */
function lazyPage<T extends { default: ComponentType }>(load: () => Promise<T>) {
  return lazy(async () => {
    try {
      const page = await load()
      sessionStorage.removeItem('flexarr.reloadedForUpdate')
      return page
    } catch (error) {
      if (!sessionStorage.getItem('flexarr.reloadedForUpdate')) {
        sessionStorage.setItem('flexarr.reloadedForUpdate', '1')
        location.reload()
        return new Promise<T>(() => {}) // the reload replaces this page
      }
      throw error
    }
  })
}

const PlayerPage = lazyPage(() => import('./pages/PlayerPage').then((m) => ({ default: m.PlayerPage })))
const SettingsPage = lazyPage(() => import('./pages/SettingsPage').then((m) => ({ default: m.SettingsPage })))

type ConnectionState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  /** `asked`: opened on purpose (Switch profile), so it can be backed out of. */
  | { kind: 'choose-profile'; profiles: Profile[]; asked: boolean; initial?: Profile }
  | { kind: 'unauthorized'; wrongKey?: boolean }
  | { kind: 'error'; message: string }

/** Everything below the connection check; rendered inside a router so tests can supply a MemoryRouter. */
export function Shell() {
  return (
    <SessionProvider>
      <LibrariesProvider>
        <AudioPlayerProvider>
          <Suspense fallback={null}>
            <Routes>
              <Route path="play/:id" element={<PlayerPage />} />
              <Route element={<Layout />}>
                <Route index element={<HomePage />} />
                <Route path="library/:id" element={<LibraryPage />} />
                <Route path="item/:id" element={<ItemPage />} />
                <Route path="person/:id" element={<PersonPage />} />
                <Route path="collections" element={<CollectionsPage />} />
                <Route path="collection/:id" element={<CollectionPage />} />
                <Route path="playlists" element={<PlaylistsPage />} />
                <Route path="playlist/:id" element={<PlaylistPage />} />
                <Route path="mix/:id" element={<MixPage />} />
                <Route path="discover" element={<DiscoverPage />} />
                <Route path="discover/:mediaType/:tmdbId" element={<ExternalTitlePage />} />
                <Route path="upcoming" element={<UpcomingPage />} />
                <Route path="link" element={<LinkPage />} />
                <Route path="settings" element={<SettingsPage />} />
                <Route path="settings/:section" element={<SettingsPage />} />
                <Route path="*" element={<p className="page-message muted">There's nothing at this address.</p>} />
              </Route>
            </Routes>
          </Suspense>
        </AudioPlayerProvider>
      </LibrariesProvider>
    </SessionProvider>
  )
}

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    migrateStoredApiKey()
      .then(() => getSystemStatus(controller.signal))
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
          sessionStorage.removeItem('flexarr.profileChosen')
          clearApiCache()
        }
        // Ask who's watching when the household has several profiles and this device hasn't chosen one, or when
        // asked to ("Switch profile"). Otherwise, over Tailscale the person is already known from their account.
        const asked = sessionStorage.getItem('flexarr.pickProfile') === '1' || (oldMainToken && me?.method === 'tailscale')
        const chosen = profileToken() !== null || sessionStorage.getItem('flexarr.profileChosen') === '1' || me?.method === 'tailscale'
        // Asked for one profile in particular (Switch to the main profile, in Settings): straight to its PIN.
        const initial = asked ? profiles.find((p) => p.id === sessionStorage.getItem('flexarr.pickProfileFor')) : undefined
        sessionStorage.removeItem('flexarr.pickProfileFor')
        setState(profiles.length > 0 && (asked || (profiles.length > 1 && !chosen)) ? { kind: 'choose-profile', profiles, asked, initial } : { kind: 'ready' })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof UnauthorizedError) setState({ kind: 'unauthorized' })
        else setState({ kind: 'error', message: error instanceof Error ? error.message : String(error) })
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

  if (state.kind === 'choose-profile') {
    return (
      <ProfilePicker
        profiles={state.profiles}
        initial={state.initial}
        onChosen={() => {
          sessionStorage.removeItem('flexarr.pickProfile')
          sessionStorage.setItem('flexarr.profileChosen', '1')
          setState({ kind: 'ready' })
        }}
        // Carry on as before: opening the picker didn't give up the profile chosen here.
        onCancel={
          state.asked
            ? () => {
                sessionStorage.removeItem('flexarr.pickProfile')
                sessionStorage.setItem('flexarr.profileChosen', '1')
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

  return (
    <div className="connect">
      <p className="wordmark">
        <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
        flexarr
      </p>
      <section className="panel" aria-label="Server status">
        {state.kind === 'loading' && <p className="muted">Connecting…</p>}
        {state.kind === 'error' && <p className="error">Can't reach the server ({state.message}). Check that Flexarr is running, then reload.</p>}
        {state.kind === 'unauthorized' && (
          <form className="api-key-form" onSubmit={submitApiKey}>
            <label htmlFor="apiKey">API key</label>
            {tvCode ? (
              // Arrived from a TV's QR code: this device has never signed in, and only a signed-in main profile can let a TV in.
              <p className="muted">
                To let your TV in, this device needs to sign in once with the API key (Settings › Server on a computer that’s signed in). Or skip it: on that
                computer, open Flexarr, go to <strong>Settings › Connect a TV</strong> and enter <span className="connect-tv-code">{tvCode}</span>
              </p>
            ) : (
              <p className="muted">Ask whoever set up Flexarr for the key, or connect from home.</p>
            )}
            <div className="api-key-row">
              <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
              <button type="submit" className="button">
                Connect
              </button>
            </div>
            {state.wrongKey && <p className="error">That isn't the right key.</p>}
            {!tvCode && (
              <p className="muted small-print">
                For whoever set it up: the key is in config.json in the server’s data folder. Opening Flexarr through Tailscale signs you in without it.
              </p>
            )}
          </form>
        )}
      </section>
    </div>
  )
}
