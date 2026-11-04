import { useEffect, type ComponentType, type ReactNode, type SVGProps } from 'react'
import { Navigate, NavLink, useParams } from 'react-router'
import { clearApiCache, getMe, getTraktStatus, getUpdates, lockAdmin } from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { availableLanguages, t, tk } from '../../i18n'
import { Antenna, Download, Film, Gauge, Globe, Headphones, History, Info, LayoutGrid, Notes, Padlock, PanelLeft, Shield, PlayCircle, Plug, Restore, Stack, Users } from '../../components/icons'
import { AboutSection } from './AboutSection'
import { SecuritySection } from './SecuritySection'
import { AdminUnlock } from './AdminUnlock'
import { BackupSection } from './BackupSection'
import { ConnectionsSection, TRAKT_CHANGED } from './ConnectionsSection'
import { ConnectTvSection } from './ConnectTvSection'
import { HealthSection } from './HealthSection'
import { ImportSection } from './ImportSection'
import { LanguageSection } from './LanguageSection'
import { LayoutSection } from './LayoutSection'
import { LibrariesSection } from './LibrariesSection'
import { LogsSection } from './LogsSection'
import { OverviewSection } from './OverviewSection'
import { PlaybackSection } from './PlaybackSection'
import { ProfilesSection } from './ProfilesSection'
import { RemoteAccessSection } from './RemoteAccessSection'
import { ListeningSection } from './ListeningSection'
import { LiveTvSection } from './LiveTvSection'
import { StreamingSection } from './StreamingSection'
import { NavigationSection } from './NavigationSection'
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

/** Every profile's own: how it watches and listens, what Home and Browse show, and its menu. */
const profilePages: Page[] = [
  { id: 'playback', label: tk('Playback'), icon: PlayCircle, render: () => <PlaybackSection /> },
  { id: 'listening', label: tk('Podcasts & audiobooks'), icon: Headphones, render: () => <ListeningSection /> },
  { id: 'layout', label: tk('Home & browse'), icon: LayoutGrid, render: () => <LayoutSection /> },
  { id: 'navigation', label: tk('Navigation'), icon: PanelLeft, render: () => <NavigationSection /> },
]

/** Listed once the server offers a language besides English (from 0.61). */
const languagePage: Page = { id: 'language', label: tk('Language'), icon: Globe, render: () => <LanguageSection /> }

/** Listed when the server offers Trakt (from 0.57). */
const traktPage: Page = { id: 'trakt', label: 'Trakt', icon: History, render: () => <TraktSection /> }

/** The server's, for the main profile only. */
const serverPages: Page[] = [
  { id: 'overview', label: tk('Overview'), icon: Gauge, render: () => <OverviewSection /> },
  { id: 'libraries', label: tk('Libraries'), icon: Stack, render: () => <LibrariesSection /> },
  { id: 'health', label: tk('Library health'), render: () => <HealthSection />, under: 'libraries' },
  { id: 'import', label: tk('Bring over from Plex'), render: () => <ImportSection />, under: 'libraries' },
  { id: 'profiles', label: tk('Profiles'), icon: Users, render: () => <ProfilesSection /> },
  { id: 'streaming', label: tk('Streaming'), icon: Film, render: () => <StreamingSection /> },
  { id: 'livetv', label: tk('Live TV'), icon: Antenna, render: () => <LiveTvSection /> },
  { id: 'connections', label: tk('Connections'), icon: Plug, render: () => <ConnectionsSection /> },
  { id: 'remote', label: tk('Remote access'), icon: Globe, render: () => <RemoteAccessSection /> },
  { id: 'security', label: tk('Security'), icon: Shield, render: () => <SecuritySection /> },
  { id: 'tv', label: tk('Connect a device'), render: () => <ConnectTvSection />, under: 'security' },
  { id: 'backups', label: tk('Backup & restore'), icon: Restore, render: () => <BackupSection /> },
  { id: 'updates', label: tk('Updates'), icon: Download, render: () => <UpdatesSection /> },
  { id: 'logs', label: tk('Logs'), icon: Notes, render: () => <LogsSection /> },
  { id: 'about', label: tk('About'), icon: Info, render: () => <AboutSection /> },
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
  sidebar: 'navigation',
  // Where a profile's requests go is in its editor in Profiles; how books are requested, in Libraries.
  requests: 'libraries',
}

export function SettingsPage() {
  const { section } = useParams()
  const me = useApi(getMe, [])
  const { profiles } = useSession()
  const current = profiles?.find((p) => p.isCurrent)
  const name = current?.name
  const trakt = useApi((signal) => getTraktStatus(signal), [])
  const reloadTrakt = trakt.reload
  useEffect(() => {
    window.addEventListener(TRAKT_CHANGED, reloadTrakt)
    return () => window.removeEventListener(TRAKT_CHANGED, reloadTrakt)
  }, [reloadTrakt])
  const languages = useApi(availableLanguages, [])
  const ownPages = [
    ...profilePages,
    ...((languages.data?.length ?? 0) > 1 ? [languagePage] : []),
    ...(trakt.data?.available ? [traktPage] : []),
  ]
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
  const badges: Record<string, string | undefined> = { updates: updates.data?.available ? t('Flexarr {version} is available', { version: updates.data.latest?.version }) : undefined }

  // A child's choices are a parent's to make: the kids app has no Settings, even by its address.
  if (me.data?.isKids) return <Navigate to="/" replace />
  // Which page to open first depends on whose profile this is, so it waits to know.
  if (!section) return me.loading ? null : <Navigate to={`/settings/${isAdmin ? 'overview' : 'playback'}`} replace />
  if (moved[section]) return <Navigate to={`/settings/${moved[section]}`} replace />

  const profilePage = [...profilePages, traktPage].find((p) => p.id === section)
  const serverPage = serverPages.find((p) => p.id === section)
  // A server page (an old link, say) waits to know whose profile this is, and sends anyone else to their own pages.
  if (!profilePage && me.loading) return null
  if (!profilePage && !isAdmin && !locked) return <Navigate to="/settings/playback" replace />
  const lit = serverPage?.under ?? section

  const link = (p: Page) => (
    <NavLink key={p.id} to={`/settings/${p.id}`} className={() => (p.id === lit ? 'active' : undefined)}>
      {p.icon && <p.icon className="settings-nav-icon" />}
      {t(p.label)}
      {badges[p.id] && <span className="settings-nav-badge" role="img" aria-label={badges[p.id]} title={badges[p.id]} />}
    </NavLink>
  )

  return (
    <div className="settings">
      <aside className="settings-nav">
        <h1>{t('Settings')}</h1>
        <nav aria-label={t('Settings')}>
          <div className="settings-nav-group">
            <h2>{profileHeading(name)}</h2>
            {ownPages.map(link)}
          </div>
          {(isAdmin || locked) && (
            <div className="settings-nav-group">
              <h2>{t('Server')}</h2>
              {isAdmin && !pinMissing ? serverPages.filter((p) => !p.under).map(link) : link({ id: 'overview', label: locked ? tk('Unlock settings') : tk('Choose an admin PIN'), icon: Padlock, render: () => null })}
              {isAdmin && unlockedUntil && (
                <button type="button" className="text-button settings-lock" onClick={() => void lockAdmin().then(() => window.location.reload())}>
                  {t('Lock settings')}
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
              : <p className="muted">{t('There’s no such page in Settings.')}</p>}
      </div>
    </div>
  )
}


/** "Claire’s profile", but a name that already says profile (a new server's "Main profile") stays as it is. */
function profileHeading(name: string | undefined) {
  if (!name) return t('Your profile')
  return /\bprofile$/i.test(name.trim()) ? name : t('{name}’s profile', { name })
}
