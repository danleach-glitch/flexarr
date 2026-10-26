import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useLocation } from 'react-router'
import { askForProfile, clearApiCache, getLiveTvStatus, getPodcastPreferences, selectProfile, signOut } from '../api/client'
import { PODCAST_PREFERENCES_CHANGED } from '../lib/podcasts'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useLibraries } from '../hooks/librariesContext'
import { useSession } from '../hooks/sessionContext'
import { Antenna, Calendar, ChevronsLeft, Close, Film, Gear, Home, Menu as MenuIcon, Microphone, Playlist, Search, Sparkles, Stack } from './icons'
import { libraryIcons } from './libraryIcons'
import { AddToCollectionHost } from './AddToCollection'
import { AddToPlaylistHost } from './AddToPlaylist'
import { NowPlaying } from './NowPlaying'
import { Avatar } from './ProfilePicker'
import { SearchPalette } from './SearchPalette'

const railKey = 'flexarr.sidebarRail'

/** The sidebar starts as an icon rail on narrower windows, until it's expanded or collapsed by hand. */
function initialRail() {
  try {
    const saved = localStorage.getItem(railKey)
    if (saved !== null) return saved === '1'
  } catch {
    // Storage blocked: fall back on the window width.
  }
  return window.innerWidth < 1200
}

export function Layout() {
  const { libraries } = useLibraries()
  const [scrolled, setScrolled] = useState(false)
  const [searching, setSearching] = useState(false)
  const [profileMenu, setProfileMenu] = useState(false)
  const [rail, setRail] = useState(initialRail)
  // Whether Podcasts has a place among the libraries (a profile can leave it out; Settings › Podcasts).
  const [podcastsInSidebar, setPodcastsInSidebar] = useState(true)
  const hasPodcasts = libraries.some((l) => l.type === 'Podcasts')
  useEffect(() => {
    if (!hasPodcasts) return
    const controller = new AbortController()
    const load = () => getPodcastPreferences(controller.signal).then((p) => setPodcastsInSidebar(p.showInSidebar), () => undefined)
    const changed = () => {
      clearApiCache((path) => path.startsWith('/podcasts/preferences'))
      void load()
    }
    void load()
    window.addEventListener(PODCAST_PREFERENCES_CHANGED, changed)
    return () => {
      controller.abort()
      window.removeEventListener(PODCAST_PREFERENCES_CHANGED, changed)
    }
  }, [hasPodcasts])
  // Podcasts aren't a library as such, but they're listed with them (last), unless the profile left them out.
  const shownLibraries = libraries.filter((l) => l.type !== 'Podcasts')
  const podcastsLink = hasPodcasts && podcastsInSidebar
  // Phones: whether the sidebar has been slid in over the page.
  const [drawer, setDrawer] = useState(false)
  const sidebar = useRef<HTMLElement>(null)
  const { me: access, profiles } = useSession()
  // Older servers have no /me; assume admin there, as before.
  const isAdmin = access?.isAdmin ?? true
  const me = profiles?.find((p) => p.isCurrent)
  const other = profiles?.length === 2 ? profiles.find((p) => !p.isCurrent) : undefined
  // Signed in with the API key (a cookie of this browser's own): any profile may end that, say on a shared laptop.
  const canSignOut = access?.method === 'session'
  const location = useLocation()
  // Live TV has a place once a source is set up (Settings › Connections); asked again on leaving Settings.
  const inSettings = location.pathname.startsWith('/settings')
  const [liveTv, setLiveTv] = useState(false)
  useEffect(() => {
    if (inSettings) return
    const controller = new AbortController()
    getLiveTvStatus(controller.signal).then((s) => setLiveTv(s.configured), () => undefined)
    return () => controller.abort()
  }, [inSettings])
  // Settings has its own sidebar, so the main one makes way and the bar carries the wordmark home instead.
  const bare = location.pathname.startsWith('/settings')
  // The page keeps clear of the now-playing bar while it's showing.
  const music = useAudioPlayer()
  const nowPlaying = music.current !== undefined || music.onMusicPage

  // The profile menu closes on a click anywhere outside it, or Escape.
  const profileAnchor = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!profileMenu) return
    const close = (e: Event) => {
      if (e instanceof KeyboardEvent ? e.key === 'Escape' : !profileAnchor.current?.contains(e.target as Node)) setProfileMenu(false)
    }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', close)
    return () => {
      document.removeEventListener('pointerdown', close)
      document.removeEventListener('keydown', close)
    }
  }, [profileMenu])

  // Labels are hidden on the rail, so its icons say what they are on hover.
  const tip = (label: string) => (rail ? label : undefined)

  const toggleRail = () => {
    setRail(!rail)
    try {
      localStorage.setItem(railKey, rail ? '0' : '1')
    } catch {
      // Not remembered, which is fine.
    }
  }

  // The drawer closes on Escape and takes focus while it's open, so the keyboard starts in it.
  useEffect(() => {
    if (!drawer) return
    sidebar.current?.querySelector<HTMLElement>('.sidebar-nav a')?.focus()
    const close = (e: KeyboardEvent) => e.key === 'Escape' && setDrawer(false)
    document.addEventListener('keydown', close)
    return () => document.removeEventListener('keydown', close)
  }, [drawer])

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 24)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  // ⌘K / Ctrl-K or "/" opens search from anywhere.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)
      if ((e.key === 'k' && (e.metaKey || e.ctrlKey)) || (e.key === '/' && !typing)) {
        e.preventDefault()
        setSearching(true)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  useEffect(() => {
    setSearching(false)
    setDrawer(false)
    window.scrollTo(0, 0)
  }, [location.pathname])

  return (
    <div className={['shell', rail && 'rail', bare && 'bare', nowPlaying && 'has-now-playing'].filter(Boolean).join(' ')}>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      {/* Over the page, beside the sidebar: search, settings and the profile on the right. Phones add the menu that
          slides the sidebar in, and the wordmark. */}
      <header className={scrolled ? 'topbar solid' : 'topbar'}>
        <button className="icon-button topbar-menu" aria-label="Menu" aria-expanded={drawer} aria-controls="sidebar" onClick={() => setDrawer(true)}>
          <MenuIcon />
        </button>
        <Link to="/" className="wordmark topbar-wordmark" aria-label="Flexarr home">
          <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
          flexarr
        </Link>
        <div className="topbar-tools">
          <button className="search-trigger" onClick={() => setSearching(true)}>
            <Search />
            <span>Search</span>
            <kbd>⌘K</kbd>
          </button>
          {/* Every profile has settings of its own; the server's are the main profile's. */}
          <NavLink to="/settings" className="icon-button" aria-label="Settings">
            <Gear />
          </NavLink>
          {me && profiles && (profiles.length > 1 || canSignOut) && (
            <div className="profile-menu-anchor" ref={profileAnchor}>
              <button className="profile-button" aria-expanded={profileMenu} aria-label={`Profile: ${me.name}`} onClick={() => setProfileMenu(!profileMenu)}>
                <Avatar profile={me} size={34} />
              </button>
              {profileMenu && (
                <div className="profile-menu" role="menu">
                  <p className="muted">Watching as {me.name}</p>
                  {profiles.length > 1 && (
                    <button
                      role="menuitem"
                      onClick={async () => {
                        // With just one other profile and no PIN to ask for, switch straight to it.
                        if (other && !other.hasPin) {
                          // Refused (over Tailscale, a main profile without a PIN): the picker says why.
                          await selectProfile(other.id).then(
                            () => sessionStorage.setItem('flexarr.profileChosen', '1'),
                            () => askForProfile(),
                          )
                        } else {
                          // Back to the picker, even over Tailscale where the account already says who's watching.
                          askForProfile()
                        }
                        // Reload everything for the new profile.
                        window.location.assign('/')
                      }}
                    >
                      {other && !other.hasPin ? `Switch to ${other.name}` : 'Switch profile'}
                    </button>
                  )}
                  {isAdmin && (
                    <Link role="menuitem" to="/settings/profiles" onClick={() => setProfileMenu(false)}>
                      Manage profiles
                    </Link>
                  )}
                  {canSignOut && (
                    <button
                      role="menuitem"
                      onClick={async () => {
                        if (!confirm('Sign out of this browser? Signing in again takes the API key.')) return
                        setProfileMenu(false)
                        // Whatever the server says, start again from the top: the sign-in page if it's needed.
                        await signOut().catch(() => undefined)
                        window.location.assign('/')
                      }}
                    >
                      Sign out of this browser
                    </button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </header>
      {drawer && !bare && <div className="sidebar-backdrop" onClick={() => setDrawer(false)} />}
      {!bare && (
        <aside id="sidebar" className={drawer ? 'sidebar open' : 'sidebar'} ref={sidebar}>
          <div className="sidebar-head">
            <Link to="/" className="wordmark" aria-label="Flexarr home">
              <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
              <span className="sidebar-label">flexarr</span>
            </Link>
            <button className="icon-button sidebar-close" aria-label="Close menu" onClick={() => setDrawer(false)}>
              <Close />
            </button>
          </div>
          {/* Libraries scroll on their own below the wordmark, however many there are. */}
          <nav aria-label="Main" className="sidebar-nav">
            <NavLink to="/" end className="sidebar-link" title={tip('Home')}>
              <Home />
              <span className="sidebar-label">Home</span>
            </NavLink>
            {(shownLibraries.length > 0 || podcastsLink) && (
              <>
                <h2 className="sidebar-heading">
                  <span className="sidebar-label">Libraries</span>
                </h2>
                {shownLibraries.map((library) => {
                  const Icon = libraryIcons[library.type] ?? Film
                  return (
                    <NavLink key={library.id} to={`/library/${library.id}`} className="sidebar-link" title={tip(library.name)}>
                      <Icon />
                      <span className="sidebar-label">{library.name}</span>
                      {library.scan && <span className="scan-dot" aria-label="Scanning" />}
                    </NavLink>
                  )
                })}
                {podcastsLink && (
                  <NavLink to="/podcasts" className="sidebar-link" title={tip('Podcasts')}>
                    <Microphone />
                    <span className="sidebar-label">Podcasts</span>
                  </NavLink>
                )}
              </>
            )}
            <h2 className="sidebar-heading">
              <span className="sidebar-label">Browse</span>
            </h2>
            <NavLink to="/collections" className="sidebar-link" title={tip('Collections')}>
              <Stack />
              <span className="sidebar-label">Collections</span>
            </NavLink>
            {libraries.some((l) => l.type === 'Music') && (
              <NavLink to="/playlists" className="sidebar-link" title={tip('Playlists')}>
                <Playlist />
                <span className="sidebar-label">Playlists</span>
              </NavLink>
            )}
            {liveTv && (
              <NavLink to="/livetv" className="sidebar-link" title={tip('Live TV')}>
                <Antenna />
                <span className="sidebar-label">Live TV</span>
              </NavLink>
            )}
            <NavLink to="/discover" className="sidebar-link" title={tip('Discover')}>
              <Sparkles />
              <span className="sidebar-label">Discover</span>
            </NavLink>
            <NavLink to="/upcoming" className="sidebar-link" title={tip('Coming soon')}>
              <Calendar />
              <span className="sidebar-label">Coming soon</span>
            </NavLink>
          </nav>
          <button className="sidebar-link sidebar-toggle" aria-expanded={!rail} title={tip('Expand sidebar')} onClick={toggleRail}>
            <ChevronsLeft style={rail ? { transform: 'scaleX(-1)' } : undefined} />
            <span className="sidebar-label">{rail ? 'Expand sidebar' : 'Collapse sidebar'}</span>
          </button>
        </aside>
      )}
      <main id="main" className="page">
        <Outlet />
        <AddToCollectionHost />
        <AddToPlaylistHost />
      </main>
      <NowPlaying />
      {searching && <SearchPalette onClose={() => setSearching(false)} />}
    </div>
  )
}
