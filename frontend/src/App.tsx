import { lazy, Suspense, useEffect, useState, type FormEvent } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router'
import { getMe, getProfiles, getSystemStatus, profileToken, saveApiKey, UnauthorizedError, type Profile } from './api/client'
import { ProfilePicker } from './components/ProfilePicker'
import { Layout } from './components/Layout'
import { AudioPlayerProvider } from './hooks/audioPlayer'
import { LibrariesProvider } from './hooks/libraries'
import { DiscoverPage } from './pages/DiscoverPage'
import { ExternalTitlePage } from './pages/ExternalTitlePage'
import { HomePage } from './pages/HomePage'
import { ItemPage } from './pages/ItemPage'
import { LibraryPage } from './pages/LibraryPage'
import { PersonPage } from './pages/PersonPage'
import { UpcomingPage } from './pages/UpcomingPage'
import './styles/tokens.css'
import './styles/base.css'
import './styles/app.css'
import './styles/detail.css'
import './styles/player.css'
import './styles/settings.css'

// The player (with hls.js) and settings load on demand to keep the first page light.
const PlayerPage = lazy(() => import('./pages/PlayerPage').then((m) => ({ default: m.PlayerPage })))
const SettingsPage = lazy(() => import('./pages/SettingsPage').then((m) => ({ default: m.SettingsPage })))

type ConnectionState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'choose-profile'; profiles: Profile[] }
  | { kind: 'unauthorized' }
  | { kind: 'error'; message: string }

/** Everything below the connection check; rendered inside a router so tests can supply a MemoryRouter. */
export function Shell() {
  return (
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
              <Route path="discover" element={<DiscoverPage />} />
              <Route path="discover/:mediaType/:tmdbId" element={<ExternalTitlePage />} />
              <Route path="upcoming" element={<UpcomingPage />} />
              <Route path="settings" element={<SettingsPage />} />
              <Route path="settings/:section" element={<SettingsPage />} />
              <Route path="*" element={<p className="page-message muted">There's nothing at this address.</p>} />
            </Route>
          </Routes>
        </Suspense>
      </AudioPlayerProvider>
    </LibrariesProvider>
  )
}

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    getSystemStatus(controller.signal)
      // An older server without profiles still works: carry on as the main profile.
      .then(() =>
        Promise.all([
          getProfiles(controller.signal).catch((e: unknown) => (e instanceof UnauthorizedError ? Promise.reject(e) : [])),
          getMe(controller.signal).catch(() => null),
        ]),
      )
      .then(([profiles, me]) => {
        // Ask who's watching when the household has several profiles and this device hasn't chosen one.
        // Over Tailscale the person is already known from their Tailscale account.
        const chosen = profileToken() !== null || sessionStorage.getItem('flexarr.profileChosen') === '1' || me?.method === 'tailscale'
        setState(profiles.length > 1 && !chosen ? { kind: 'choose-profile', profiles } : { kind: 'ready' })
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof UnauthorizedError) setState({ kind: 'unauthorized' })
        else setState({ kind: 'error', message: error instanceof Error ? error.message : String(error) })
      })
    return () => controller.abort()
  }, [attempt])

  function submitApiKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    saveApiKey(key)
    setState({ kind: 'loading' })
    setAttempt((n) => n + 1)
  }

  if (state.kind === 'choose-profile') {
    return (
      <ProfilePicker
        profiles={state.profiles}
        onChosen={() => {
          sessionStorage.setItem('flexarr.profileChosen', '1')
          setState({ kind: 'ready' })
        }}
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
      <p className="wordmark">flexarr</p>
      <section className="panel" aria-label="Server status">
        {state.kind === 'loading' && <p className="muted">Connecting…</p>}
        {state.kind === 'error' && <p className="error">Can't reach the server ({state.message}). Check that Flexarr is running, then reload.</p>}
        {state.kind === 'unauthorized' && (
          <form className="api-key-form" onSubmit={submitApiKey}>
            <label htmlFor="apiKey">API key</label>
            <p className="muted">
              It's in config.json in the server's data folder. Away from home, open Flexarr through Tailscale instead and you'll be signed in automatically.
            </p>
            <div className="api-key-row">
              <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
              <button type="submit" className="button">
                Connect
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  )
}
