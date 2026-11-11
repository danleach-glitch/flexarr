import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useLocation } from 'react-router'
import { askForProfile, asksPin, clearApiCache, getLiveTvStatus, getPodcastPreferences, selectProfile, signOut } from '../api/client'
import { PODCAST_PREFERENCES_CHANGED } from '../lib/podcasts'
import { session } from '../lib/session'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useLibraries } from '../hooks/librariesContext'
import { useSession } from '../hooks/sessionContext'
import { NoticeHost } from './Notice'
import { useConfirm } from '../hooks/useConfirm'
import { trapTab } from '../hooks/useModal'
import { ChevronsLeft, Close, Menu as MenuIcon, Playlist } from './icons'
import { icon } from './icons.generated'
import { libraryIcons } from './libraryIcons'
import { AsyncButton } from './AsyncButton'
import { AddToCollectionHost } from './AddToCollection'
import { AddToPlaylistHost } from './AddToPlaylist'
import { NowPlaying } from './NowPlaying'
import { PairingPrompt } from './PairingPrompt'
import { UnlockPrompt } from './UnlockDialog'
import { Avatar } from './ProfilePicker'
import { KidsLockMenuItems } from './KidsLock'
import { SearchPalette } from './SearchPalette'
import { t, Words } from '../i18n'

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
  const [ask, confirmDialog] = useConfirm()
  const [rail, setRail] = useState(initialRail)
  // Whether Podcasts has a place among the libraries (a profile can leave it out; Settings › Navigation).
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
  // Podcasts aren't a library as such, but they're listed with them in the profile's order (Settings › Navigation), unless
  // the profile left them out.
  const podcastsLink = hasPodcasts && podcastsInSidebar
  const shownLibraries = libraries.filter((l) => l.type !== 'Podcasts' || podcastsLink)
  // Phones: whether the sidebar has been slid in over the page.
  const [drawer, setDrawer] = useState(false)
  const sidebar = useRef<HTMLElement>(null)
  const { me: access, profiles } = useSession()
  // Older servers have no /me; assume admin there, as before.
  const isAdmin = access?.isAdmin ?? true
  const me = profiles?.find((p) => p.isCurrent)
  const other = profiles?.length === 2 ? profiles.find((p) => !p.isCurrent) : undefined
  // A child's profile has the simpler app: no Settings, no signing the browser out. Any age limit keeps a profile out
  // of Discover and Coming soon, whose titles Flexarr hasn't got and so can't tell the age of.
  const kids = access?.isKids === true
  const limited = access?.maxRatingAge != null
  const hasMusic = libraries.some((l) => l.type === 'Music')
  // What the profile left out of its menu (Settings › Navigation); reachable still from Home and links.
  const hidden = new Set(me?.hiddenNavigation ?? [])
  const shows = {
    watchlist: !kids && !hidden.has('watchlist'),
    collections: !kids && !hidden.has('collections'),
    playlists: hasMusic && !hidden.has('playlists'),
    discover: !limited && !hidden.has('discover'),
    upcoming: !limited && !hidden.has('upcoming'),
  }
  // Signed in with the API key (a cookie of this browser's own): any profile may end that, say on a shared laptop.
  const canSignOut = access?.method === 'session' && !kids
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
  const liveTvLink = liveTv && !hidden.has('live-tv')
  // Settings has its own sidebar, so the main one makes way and the bar carries the wordmark home instead.
  const bare = location.pathname.startsWith('/settings')
  // The page keeps clear of the now-playing bar while it's showing.
  const music = useAudioPlayer()
  const nowPlaying = music.current !== undefined

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

  // The drawer closes on Escape and takes focus while it's open, so the keyboard starts in it; Tab stays in it, as in a
  // dialog. Closed, focus goes back to the menu button rather than staying on a link slid out of sight.
  const menuButton = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!drawer) return
    const drawn = sidebar.current
    drawn?.querySelector<HTMLElement>('.sidebar-nav a')?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setDrawer(false)
      else if (drawn) trapTab(drawn, e)
    }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      const focused = document.activeElement
      if (!focused || focused === document.body || drawn?.contains(focused)) menuButton.current?.focus()
    }
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

  // A new page closes search and the drawer (while rendering, rather than in an effect that renders again) and starts
  // at the top.
  const [pagePath, setPagePath] = useState(location.pathname)
  if (pagePath !== location.pathname) {
    setPagePath(location.pathname)
    setSearching(false)
    setDrawer(false)
  }
  // A block, not `() => window.scrollTo(…)`: Chrome's scrollTo now returns a Promise, which React would take for the
  // effect's clean-up and call (`destroy is not a function`) on the next page, or at once under StrictMode in dev.
  useEffect(() => {
    window.scrollTo(0, 0)
  }, [location.pathname])

  return (
    <div className={['shell', rail && 'rail', bare && 'bare', nowPlaying && 'has-now-playing', kids && 'kids'].filter(Boolean).join(' ')}>
      <a className="skip-link" href="#main">
        {t('Skip to content')}
      </a>
      {/* Over the page, beside the sidebar: search, settings and the profile on the right. Phones add the menu that
          slides the sidebar in, and the wordmark. */}
      <header className={scrolled ? 'topbar solid' : 'topbar'}>
        <button ref={menuButton} className="icon-button topbar-menu" aria-label={t('Menu')} aria-expanded={drawer} aria-controls="sidebar" onClick={() => setDrawer(true)}>
          <MenuIcon />
        </button>
        <Link to="/" className="wordmark topbar-wordmark" aria-label={t('Flexarr home')}>
          <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
          flexarr
        </Link>
        <div className="topbar-tools">
          <button className="search-trigger" onClick={() => setSearching(true)}>
            <icon.search />
            <span>{t(Words.search)}</span>
            <kbd>⌘K</kbd>
          </button>
          {/* Every profile has settings of its own; the server's are the main profile's. */}
          {!kids && (
            <NavLink to="/settings" className="icon-button" aria-label={t(Words.settings)}>
              <icon.settings />
            </NavLink>
          )}
          {me && profiles && (profiles.length > 1 || canSignOut || access?.kidsLock) && (
            <div className="profile-menu-anchor" ref={profileAnchor}>
              <button className="profile-button" aria-expanded={profileMenu} aria-label={t('Profile: {name}', { name: me.name })} onClick={() => setProfileMenu(!profileMenu)}>
                <Avatar profile={me} size={34} />
              </button>
              {profileMenu && (
                <div className="profile-menu" role="menu">
                  <p className="muted">{t('Watching as {name}', { name: me.name })}</p>
                  {profiles.length > 1 && (
                    <AsyncButton
                      role="menuitem"
                      onClick={async () => {
                        // With just one other profile and no PIN to ask for, switch straight to it.
                        if (other && !asksPin(other)) {
                          // Refused (over Tailscale, a main profile without a PIN): the picker says why.
                          await selectProfile(other.id).then(
                            () => session.set('flexarr.profileChosen', '1'),
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
                      {other && !asksPin(other) ? t('Switch to {name}', { name: other.name }) : t('Switch profile')}
                    </AsyncButton>
                  )}
                  {(isAdmin || access?.canUnlock) && (
                    <Link role="menuitem" to="/settings/profiles" onClick={() => setProfileMenu(false)}>
                      {t('Manage profiles')}
                    </Link>
                  )}
                  <KidsLockMenuItems access={access} profiles={profiles} onClose={() => setProfileMenu(false)} />
                  {canSignOut && (
                    <button
                      role="menuitem"
                      onClick={async () => {
                        setProfileMenu(false)
                        if (!(await ask({ title: t('Sign out of this browser? Signing in again takes the API key.'), confirmLabel: t('Sign out'), danger: true }))) return
                        // Whatever the server says, start again from the top: the sign-in page if it's needed.
                        await signOut().catch(() => undefined)
                        window.location.assign('/')
                      }}
                    >
                      {t('Sign out of this browser')}
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
            <Link to="/" className="wordmark" aria-label={t('Flexarr home')}>
              <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
              <span className="sidebar-label">flexarr</span>
            </Link>
            <button className="icon-button sidebar-close" aria-label={t('Close menu')} onClick={() => setDrawer(false)}>
              <Close />
            </button>
          </div>
          {/* Libraries scroll on their own below the wordmark, however many there are. */}
          <nav aria-label={t('Main')} className="sidebar-nav">
            <NavLink to="/" end className="sidebar-link" title={tip(t(Words.home))}>
              <icon.home />
              <span className="sidebar-label">{t(Words.home)}</span>
            </NavLink>
            {shownLibraries.length > 0 && (
              <>
                <h2 className="sidebar-heading">
                  <span className="sidebar-label">{t(Words.libraries)}</span>
                </h2>
                {shownLibraries.map((library) => {
                  if (library.type === 'Podcasts')
                    return (
                      <NavLink key={library.id} to="/podcasts" className="sidebar-link" title={tip(t(Words.podcasts))}>
                        <icon.podcasts />
                        <span className="sidebar-label">{t(Words.podcasts)}</span>
                      </NavLink>
                    )
                  const Icon = libraryIcons[library.type] ?? icon.movies
                  return (
                    <NavLink key={library.id} to={`/library/${library.id}`} className="sidebar-link" title={tip(library.name)}>
                      <Icon />
                      <span className="sidebar-label">{library.name}</span>
                      {library.scan && <span className="scan-dot" aria-label={t('Scanning')} />}
                    </NavLink>
                  )
                })}
              </>
            )}
            {(Object.values(shows).some(Boolean) || liveTvLink) && (
              <h2 className="sidebar-heading">
                <span className="sidebar-label">{t(Words.browse)}</span>
              </h2>
            )}
            {/* A child's profile has no watchlist or collections. */}
            {shows.watchlist && (
              <NavLink to="/watchlist" className="sidebar-link" title={tip(t(Words.watchlist))}>
                <icon.watchlist />
                <span className="sidebar-label">{t(Words.watchlist)}</span>
              </NavLink>
            )}
            {shows.collections && (
              <NavLink to="/collections" className="sidebar-link" title={tip(t(Words.collections))}>
                <icon.collections />
                <span className="sidebar-label">{t(Words.collections)}</span>
              </NavLink>
            )}
            {shows.playlists && (
              <NavLink to="/playlists" className="sidebar-link" title={tip(t('Playlists'))}>
                <Playlist />
                <span className="sidebar-label">{t('Playlists')}</span>
              </NavLink>
            )}
            {liveTvLink && (
              <NavLink to="/livetv" className="sidebar-link" title={tip(t(Words.liveTv))}>
                <icon.liveTv />
                <span className="sidebar-label">{t(Words.liveTv)}</span>
              </NavLink>
            )}
            {shows.discover && (
              <NavLink to="/discover" className="sidebar-link" title={tip(t(Words.discover))}>
                <icon.discover />
                <span className="sidebar-label">{t(Words.discover)}</span>
              </NavLink>
            )}
            {shows.upcoming && (
              <NavLink to="/upcoming" className="sidebar-link" title={tip(t(Words.comingSoon))}>
                <icon.comingSoon />
                <span className="sidebar-label">{t(Words.comingSoon)}</span>
              </NavLink>
            )}
          </nav>
          <button className="sidebar-link sidebar-toggle" aria-expanded={!rail} title={tip(t('Expand sidebar'))} onClick={toggleRail}>
            <ChevronsLeft style={rail ? { transform: 'scaleX(-1)' } : undefined} />
            <span className="sidebar-label">{rail ? t('Expand sidebar') : t('Collapse sidebar')}</span>
          </button>
        </aside>
      )}
      <main id="main" className="page">
        <PairingPrompt />
        <Outlet />
        <NoticeHost />
        {confirmDialog}
        <AddToCollectionHost />
        <AddToPlaylistHost />
      </main>
      <NowPlaying />
      <UnlockPrompt />
      {searching && <SearchPalette onClose={() => setSearching(false)} />}
    </div>
  )
}
