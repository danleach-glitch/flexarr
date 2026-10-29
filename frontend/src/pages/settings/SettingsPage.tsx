import { useState, type FormEvent, type ReactNode } from 'react'
import { Navigate, NavLink, useParams } from 'react-router'
import { askForProfile, getMe, getRequestDestinations, getUpdates, signIn, UnauthorizedError, getProfiles, type Profile } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { hasRequestChoice } from '../../lib/requestLibraries'
import { AboutSection } from './AboutSection'
import { AccessSection } from './AccessSection'
import { BackupSection } from './BackupSection'
import { ConnectionsSection } from './ConnectionsSection'
import { ConnectTvSection } from './ConnectTvSection'
import { HealthSection } from './HealthSection'
import { ImportSection } from './ImportSection'
import { LayoutSection } from './LayoutSection'
import { LibrariesSection } from './LibrariesSection'
import { LogsSection } from './LogsSection'
import { OverviewSection } from './OverviewSection'
import { PlaybackSection } from './PlaybackSection'
import { PodcastsSection } from './PodcastsSection'
import { ProfilesSection } from './ProfilesSection'
import { RequestsSection } from './RequestsSection'
import { LiveTvSection } from './LiveTvSection'
import { StreamingSection } from './StreamingSection'
import { SidebarSection } from './SidebarSection'
import { UpdatesSection } from './UpdatesSection'

interface Page {
  id: string
  label: string
  render: () => ReactNode
  /** A page reached from another one, which stays lit in the list while it's open. */
  under?: string
}

/** Every profile's own: how it watches and listens, and this browser's music. */
const profilePages: Page[] = [
  { id: 'playback', label: 'Playback', render: () => <PlaybackSection /> },
  { id: 'podcasts', label: 'Podcasts', render: () => <PodcastsSection /> },
  { id: 'sidebar', label: 'Sidebar', render: () => <SidebarSection /> },
]

/** Listed only when there's a choice to make: two TV or film libraries Seerr can add requests to. */
const requestsPage: Page = { id: 'requests', label: 'Requests', render: () => <RequestsSection /> }

/** The server's, for the main profile only. */
const serverPages: Page[] = [
  { id: 'overview', label: 'Overview', render: () => <OverviewSection /> },
  { id: 'libraries', label: 'Libraries', render: () => <LibrariesSection /> },
  { id: 'health', label: 'Library health', render: () => <HealthSection />, under: 'libraries' },
  { id: 'import', label: 'Bring over from Plex', render: () => <ImportSection />, under: 'libraries' },
  { id: 'profiles', label: 'Profiles', render: () => <ProfilesSection /> },
  { id: 'layout', label: 'Home and Browse', render: () => <LayoutSection /> },
  { id: 'streaming', label: 'Streaming', render: () => <StreamingSection /> },
  { id: 'livetv', label: 'Live TV', render: () => <LiveTvSection /> },
  { id: 'connections', label: 'Connections', render: () => <ConnectionsSection /> },
  { id: 'access', label: 'Access', render: () => <AccessSection /> },
  { id: 'tv', label: 'Connect a device', render: () => <ConnectTvSection />, under: 'access' },
  { id: 'backups', label: 'Backup and restore', render: () => <BackupSection /> },
  { id: 'updates', label: 'Updates', render: () => <UpdatesSection /> },
  { id: 'logs', label: 'Logs', render: () => <LogsSection /> },
  { id: 'about', label: 'About', render: () => <AboutSection /> },
]

/** Pages that moved, so old links and bookmarks still land somewhere sensible. */
const moved: Record<string, string> = { activity: 'overview', server: 'access', remote: 'access', changelog: 'about' }

export function SettingsPage() {
  const { section } = useParams()
  const me = useApi(getMe, [])
  const { profiles } = useSession()
  const current = profiles?.find((p) => p.isCurrent)
  const name = current?.name
  const destinations = useApi(getRequestDestinations, [])
  const ownPages = destinations.data && hasRequestChoice(destinations.data, current?.hiddenLibraryIds ?? []) ? [...profilePages, requestsPage] : profilePages
  // Settings for the server are the main profile's: another profile chosen on this browser has only its own pages. The
  // owner over Tailscale is the exception, with one page saying how to prove it's them. Older servers have no /me:
  // everything, as before.
  const isAdmin = me.data?.isAdmin ?? true
  const ownerOverTailscale = !isAdmin && me.data?.method === 'tailscale' && me.data.profileId === 'owner'
  // A dot on Updates when there's a release to install. Only the main profile may ask; older servers have no /updates.
  const updates = useApi((signal) => (me.data?.isAdmin ? getUpdates(signal) : Promise.resolve(undefined)), [me.data?.isAdmin])
  const badges: Record<string, string | undefined> = { updates: updates.data?.available ? `Flexarr ${updates.data.latest?.version} is available` : undefined }

  // Which page to open first depends on whose profile this is, so it waits to know.
  if (!section) return me.loading ? null : <Navigate to={`/settings/${isAdmin ? 'overview' : 'playback'}`} replace />
  if (moved[section]) return <Navigate to={`/settings/${moved[section]}`} replace />

  const profilePage = [...profilePages, requestsPage].find((p) => p.id === section)
  const serverPage = serverPages.find((p) => p.id === section)
  // A server page (an old link, say) waits to know whose profile this is, and sends anyone else to their own pages.
  if (!profilePage && me.loading) return null
  if (!profilePage && !isAdmin && !ownerOverTailscale) return <Navigate to="/settings/playback" replace />
  const lit = serverPage?.under ?? section

  const link = (p: Page) => (
    <NavLink key={p.id} to={`/settings/${p.id}`} className={() => (p.id === lit ? 'active' : undefined)}>
      {p.label}
      {badges[p.id] && <span className="settings-nav-badge" role="img" aria-label={badges[p.id]} title={badges[p.id]} />}
    </NavLink>
  )

  return (
    <div className="settings">
      <aside className="settings-nav">
        <h1>Settings</h1>
        <nav aria-label="Settings">
          <div className="settings-nav-group">
            <h2>{profileHeading(name)}</h2>
            {ownPages.map(link)}
          </div>
          {(isAdmin || ownerOverTailscale) && (
            <div className="settings-nav-group">
              <h2>Server</h2>
              {isAdmin ? serverPages.filter((p) => !p.under).map(link) : link({ id: 'overview', label: 'Manage the server', render: () => null })}
            </div>
          )}
        </nav>
      </aside>
      <div className="settings-content">
        {profilePage
          ? profilePage.render()
          : ownerOverTailscale
            ? <TailscaleAdminNote />
            : serverPage
              ? serverPage.render()
              : <p className="muted">There’s no such page in Settings.</p>}
      </div>
    </div>
  )
}


/** "Claire’s profile", but a name that already says profile (a new server's "Main profile") stays as it is. */
function profileHeading(name: string | undefined) {
  if (!name) return 'Your profile'
  return /\bprofile$/i.test(name.trim()) ? name : `${name}’s profile`
}

/** Asks for the main profile (its PIN, if it has one), then comes back to this page of Settings. */
function switchToMainProfile(main: Profile | undefined) {
  askForProfile(main?.id)
  window.location.reload()
}

/**
 * Over Tailscale the owner's account watches as the main profile but doesn't manage the server on its own: another
 * program on the server could pose as it. The main profile's PIN, or the API key, proves it's really them.
 */
function TailscaleAdminNote() {
  const [error, setError] = useState<string>()
  const [signingIn, setSigningIn] = useState(false)
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const mainHasPin = main?.hasPin ?? false

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    setError(undefined)
    setSigningIn(true)
    signIn(key)
      .then(() => window.location.reload())
      .catch((e: unknown) => {
        setError(e instanceof UnauthorizedError ? 'That isn’t the right key.' : String(e))
        setSigningIn(false)
      })
  }

  return (
    <div className="panel">
      <p className="settings-lede">
        Over Tailscale, changing settings takes one more step, because another program on the server could pose as your Tailscale account.{' '}
        {mainHasPin
          ? 'Choose the main profile with its PIN, and this browser stays signed in until it’s signed out or left unused for two weeks.'
          : 'Ask whoever manages Flexarr to give the main profile a PIN, or sign in with the API key below.'}
      </p>
      {mainHasPin && (
        <p>
          <button className="button" onClick={() => switchToMainProfile(main)}>
            Choose the main profile
          </button>
        </p>
      )}
      <p className="settings-lede">{mainHasPin ? 'Or sign this browser in with the API key:' : 'Sign this browser in with the API key:'}</p>
      <form className="api-key-row" onSubmit={submit}>
        <input name="apiKey" type="password" autoComplete="off" aria-label="API key" required />
        <AsyncButton type="submit" className="button" busy={signingIn}>
          Sign in
        </AsyncButton>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
