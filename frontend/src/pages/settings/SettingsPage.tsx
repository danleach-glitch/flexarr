import { useEffect, type ReactNode } from 'react'
import { Navigate, NavLink, useParams } from 'react-router'
import { clearApiCache, getMe, getRequestDestinations, getUpdates, lockAdmin } from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { hasRequestChoice } from '../../lib/requestLibraries'
import { AboutSection } from './AboutSection'
import { AccessSection } from './AccessSection'
import { SecuritySection } from './SecuritySection'
import { AdminUnlock } from './AdminUnlock'
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
  { id: 'security', label: 'Security', render: () => <SecuritySection /> },
  { id: 'tv', label: 'Connect a device', render: () => <ConnectTvSection />, under: 'security' },
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
  // Settings for the server are the main profile's, and changing them takes the admin PIN (given lately on this browser):
  // locked, the server's pages ask for it. Another profile chosen here has only its own pages. Older servers have no
  // /me: everything, as before.
  const isAdmin = me.data?.isAdmin ?? true
  const locked = !isAdmin && me.data?.canUnlock === true
  // Signed in with the key on a server set up before setup asked for a PIN: one first, before anything else.
  const pinMissing = me.data?.adminPinMissing === true
  // The unlock lapses on the server; ask again then (a change since will have moved it on).
  const unlockedUntil = me.data?.unlockedUntil
  const reloadMe = me.reload
  useEffect(() => {
    if (!unlockedUntil) return
    // At least a minute: a browser clock ahead of the server's would otherwise ask again every second.
    const timer = window.setTimeout(() => {
      clearApiCache()
      reloadMe()
    }, Math.max(60_000, new Date(unlockedUntil).getTime() - Date.now() + 1000))
    return () => window.clearTimeout(timer)
  }, [unlockedUntil, reloadMe])
  // A dot on Updates when there's a release to install. Only the main profile may ask; older servers have no /updates.
  const updates = useApi((signal) => (me.data?.isAdmin ? getUpdates(signal) : Promise.resolve(undefined)), [me.data?.isAdmin])
  const badges: Record<string, string | undefined> = { updates: updates.data?.available ? `Flexarr ${updates.data.latest?.version} is available` : undefined }

  // A child's choices are a parent's to make: the kids app has no Settings, even by its address.
  if (me.data?.isKids) return <Navigate to="/" replace />
  // Which page to open first depends on whose profile this is, so it waits to know.
  if (!section) return me.loading ? null : <Navigate to={`/settings/${isAdmin ? 'overview' : 'playback'}`} replace />
  if (moved[section]) return <Navigate to={`/settings/${moved[section]}`} replace />

  const profilePage = [...profilePages, requestsPage].find((p) => p.id === section)
  const serverPage = serverPages.find((p) => p.id === section)
  // A server page (an old link, say) waits to know whose profile this is, and sends anyone else to their own pages.
  if (!profilePage && me.loading) return null
  if (!profilePage && !isAdmin && !locked) return <Navigate to="/settings/playback" replace />
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
          {(isAdmin || locked) && (
            <div className="settings-nav-group">
              <h2>Server</h2>
              {isAdmin && !pinMissing ? serverPages.filter((p) => !p.under).map(link) : link({ id: 'overview', label: locked ? 'Unlock settings' : 'Choose an admin PIN', render: () => null })}
              {isAdmin && unlockedUntil && (
                <button type="button" className="text-button settings-lock" onClick={() => void lockAdmin().then(() => window.location.reload())}>
                  Lock settings
                </button>
              )}
            </div>
          )}
        </nav>
      </aside>
      <div className="settings-content">
        {profilePage
          ? profilePage.render()
          : locked || pinMissing
            ? <AdminUnlock pinMissing={pinMissing} onUnlocked={() => window.location.reload()} />
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
