import { useEffect, type ComponentType, type ReactNode, type SVGProps } from 'react'
import { Navigate, NavLink, useParams } from 'react-router'
import { clearApiCache, getMe, getRequestDestinations, getRequestStatus, getTraktStatus, getUpdates, lockAdmin } from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { Antenna, Download, Film, Gauge, Globe, Headphones, History, Inbox, Info, LayoutGrid, Notes, Padlock, PanelLeft, Shield, PlayCircle, Plug, Restore, Stack, Users } from '../../components/icons'
import { hasRequestChoice } from '../../lib/requestLibraries'
import { AboutSection } from './AboutSection'
import { SecuritySection } from './SecuritySection'
import { AdminUnlock } from './AdminUnlock'
import { BackupSection } from './BackupSection'
import { ConnectionsSection, TRAKT_CHANGED } from './ConnectionsSection'
import { ConnectTvSection } from './ConnectTvSection'
import { HealthSection } from './HealthSection'
import { ImportSection } from './ImportSection'
import { LayoutSection } from './LayoutSection'
import { LibrariesSection } from './LibrariesSection'
import { LogsSection } from './LogsSection'
import { OverviewSection } from './OverviewSection'
import { PlaybackSection } from './PlaybackSection'
import { ProfilesSection } from './ProfilesSection'
import { RemoteAccessSection } from './RemoteAccessSection'
import { RequestsSection } from './RequestsSection'
import { ListeningSection } from './ListeningSection'
import { LiveTvSection } from './LiveTvSection'
import { StreamingSection } from './StreamingSection'
import { SidebarSection } from './SidebarSection'
import { TraktSection } from './TraktSection'
import { UpdatesSection } from './UpdatesSection'

interface Page {
  id: string
  label: string
  /** Its mark in the list. Pages reached from another one have none: they're never listed. */
  icon?: ComponentType<SVGProps<SVGSVGElement>>
  render: () => ReactNode
  /** A page reached from another one, which stays lit in the list while it's open. */
  under?: string
}

/** Every profile's own: how it watches and listens, and this browser's music. */
const profilePages: Page[] = [
  { id: 'playback', label: 'Playback', icon: PlayCircle, render: () => <PlaybackSection /> },
  { id: 'listening', label: 'Podcasts and audiobooks', icon: Headphones, render: () => <ListeningSection /> },
  { id: 'sidebar', label: 'Sidebar', icon: PanelLeft, render: () => <SidebarSection /> },
]

/** Listed when the server offers Trakt (from 0.57). */
const traktPage: Page = { id: 'trakt', label: 'Trakt', icon: History, render: () => <TraktSection /> }

/** Listed only when there's a choice to make: two TV or film libraries Seerr can add requests to. */
const requestsPage: Page = { id: 'requests', label: 'Requests', icon: Inbox, render: () => <RequestsSection /> }

/** The server's, for the main profile only. */
const serverPages: Page[] = [
  { id: 'overview', label: 'Overview', icon: Gauge, render: () => <OverviewSection /> },
  { id: 'libraries', label: 'Libraries', icon: Stack, render: () => <LibrariesSection /> },
  { id: 'health', label: 'Library health', render: () => <HealthSection />, under: 'libraries' },
  { id: 'import', label: 'Bring over from Plex', render: () => <ImportSection />, under: 'libraries' },
  { id: 'profiles', label: 'Profiles', icon: Users, render: () => <ProfilesSection /> },
  { id: 'layout', label: 'Home and Browse', icon: LayoutGrid, render: () => <LayoutSection /> },
  { id: 'streaming', label: 'Streaming', icon: Film, render: () => <StreamingSection /> },
  { id: 'livetv', label: 'Live TV', icon: Antenna, render: () => <LiveTvSection /> },
  { id: 'connections', label: 'Connections', icon: Plug, render: () => <ConnectionsSection /> },
  { id: 'remote', label: 'Remote access', icon: Globe, render: () => <RemoteAccessSection /> },
  { id: 'security', label: 'Security', icon: Shield, render: () => <SecuritySection /> },
  { id: 'tv', label: 'Connect a device', render: () => <ConnectTvSection />, under: 'security' },
  { id: 'backups', label: 'Backup and restore', icon: Restore, render: () => <BackupSection /> },
  { id: 'updates', label: 'Updates', icon: Download, render: () => <UpdatesSection /> },
  { id: 'logs', label: 'Logs', icon: Notes, render: () => <LogsSection /> },
  { id: 'about', label: 'About', icon: Info, render: () => <AboutSection /> },
]

/** Pages that moved, so old links and bookmarks still land somewhere sensible. */
const moved: Record<string, string> = {
  activity: 'overview',
  server: 'remote',
  access: 'remote',
  devices: 'security',
  changelog: 'about',
  podcasts: 'listening',
  audiobooks: 'listening',
}

export function SettingsPage() {
  const { section } = useParams()
  const me = useApi(getMe, [])
  const { profiles } = useSession()
  const current = profiles?.find((p) => p.isCurrent)
  const name = current?.name
  const destinations = useApi(getRequestDestinations, [])
  const requestStatus = useApi((signal) => getRequestStatus(signal).catch(() => undefined), [])
  const trakt = useApi((signal) => getTraktStatus(signal), [])
  const reloadTrakt = trakt.reload
  useEffect(() => {
    window.addEventListener(TRAKT_CHANGED, reloadTrakt)
    return () => window.removeEventListener(TRAKT_CHANGED, reloadTrakt)
  }, [reloadTrakt])
  const withTrakt = trakt.data?.available ? [...profilePages, traktPage] : profilePages
  // Book requests have a setup of their own (and a library to choose, with two), so they list the page too.
  const requestsListed = (destinations.data && hasRequestChoice(destinations.data, current?.hiddenLibraryIds ?? [])) || requestStatus.data?.books === true
  const ownPages = requestsListed ? [...withTrakt, requestsPage] : withTrakt
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

  const profilePage = [...profilePages, traktPage, requestsPage].find((p) => p.id === section)
  const serverPage = serverPages.find((p) => p.id === section)
  // A server page (an old link, say) waits to know whose profile this is, and sends anyone else to their own pages.
  if (!profilePage && me.loading) return null
  if (!profilePage && !isAdmin && !locked) return <Navigate to="/settings/playback" replace />
  const lit = serverPage?.under ?? section

  const link = (p: Page) => (
    <NavLink key={p.id} to={`/settings/${p.id}`} className={() => (p.id === lit ? 'active' : undefined)}>
      {p.icon && <p.icon className="settings-nav-icon" />}
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
              {isAdmin && !pinMissing ? serverPages.filter((p) => !p.under).map(link) : link({ id: 'overview', label: locked ? 'Unlock settings' : 'Choose an admin PIN', icon: Padlock, render: () => null })}
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
