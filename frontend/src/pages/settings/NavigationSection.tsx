import { useEffect, useRef, useState } from 'react'
import {
  clearApiCache,
  getLiveTvStatus,
  getPodcastPreferences,
  getProfiles,
  PROFILES_CHANGED,
  savePodcastPreferences,
  setHiddenNavigation,
  setLibraryOrder,
  type NavigationItem,
  type Profile,
} from '../../api/client'
import { useAgeLimits } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { useLibraries } from '../../hooks/librariesContext'
import { reorderedLibraries } from '../../lib/libraryOrder'
import { PODCAST_PREFERENCES_CHANGED } from '../../lib/podcasts'
import { RowList } from './LayoutSection'
import { SaveStatus, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

/** The profile's own: the order of its libraries in the sidebar here and in every app's menu, and what else is in it. */
export function NavigationSection() {
  const profiles = useApi(getProfiles, [])
  const { libraries, reload } = useLibraries()
  // The order moved to here, until the library list comes back in it.
  const [draft, setDraft] = useState<string[]>()
  const [error, setError] = useState<string>()
  // Saved a moment after the last move, so a library moved several places is one save, not several.
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  // What was last saved from here, ahead of the (cached) profiles list.
  const saved = useRef<string[]>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

  const podcastsShown = usePodcastsInSidebar()
  const me = profiles.data?.find((p) => p.isCurrent)
  if (profiles.error) return <SaveStatus error={errorText(profiles.error)} />
  if (!me) return null

  const byId = new Map(libraries.map((l) => [l.id, l]))
  const shown = draft?.filter((id) => byId.has(id)).map((id) => byId.get(id)!) ?? libraries

  const change = (ids: string[]) => {
    setDraft(ids)
    setError(undefined)
    clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      const order = reorderedLibraries(saved.current ?? me.libraryOrder ?? [], ids)
      setLibraryOrder(me.id, order).then(
        () => {
          saved.current = order
          clearApiCache((path) => path.startsWith('/profiles'))
          reload()
        },
        (e: unknown) => {
          setDraft(undefined)
          setError(errorText(e))
        },
      )
    }, 600)
  }

  return (
    <section>
      <SettingsHeader title="Navigation">The sidebar here and the apps’ menus. Drag a library to move it.</SettingsHeader>
      {shown.length > 1 ? (
        <RowList
          title={`${me.name}’s libraries`}
          // Podcasts' switch: whether it's in the web sidebar at all (Home's New podcasts row is in Home & browse).
          // Its switch only reaches the web sidebar, so its name says so.
          rows={shown.map((l) => ({
            id: l.id,
            label: l.type === 'Podcasts' && podcastsShown.on !== undefined ? `${l.name} (on the web)` : l.name,
            enabled: l.type !== 'Podcasts' || podcastsShown.on !== false,
          }))}
          onChange={(rows) => {
            const podcasts = rows.find((r) => byId.get(r.id)?.type === 'Podcasts')
            if (podcasts && podcastsShown.on !== undefined && podcasts.enabled !== podcastsShown.on) return podcastsShown.change(podcasts.enabled)
            change(rows.map((r) => r.id))
          }}
          switches={(row) => byId.get(row.id)?.type === 'Podcasts' && podcastsShown.on !== undefined}
        />
      ) : (
        <p className="muted">With two or more libraries, you can put them in order here.</p>
      )}
      {/* Only a failed save says anything, under the list so the list never moves. */}
      <SaveStatus error={error ?? podcastsShown.error} />
      {/* Podcasts alone have no list to sit in: their switch on its own. */}
      {shown.length === 1 && shown[0].type === 'Podcasts' && podcastsShown.on !== undefined && (
        <SettingsGroup title="Podcasts">
          <ToggleRow label="Show in the sidebar" help="On the web." checked={podcastsShown.on} onChange={(v) => void podcastsShown.change(v)} />
        </SettingsGroup>
      )}
      <BrowseItems profile={me} />
    </section>
  )
}

const items: { id: NavigationItem; label: string }[] = [
  { id: 'watchlist', label: 'Watchlist' },
  { id: 'collections', label: 'Collections' },
  { id: 'playlists', label: 'Playlists' },
  { id: 'live-tv', label: 'Live TV' },
  { id: 'discover', label: 'Discover' },
  { id: 'upcoming', label: 'Coming soon' },
]

/** The rest of the menu, each on or off: the ones this profile has (Playlists with music, Live TV once it's set up). */
function BrowseItems({ profile }: { profile: Profile }) {
  const { libraries } = useLibraries()
  const { limited } = useAgeLimits()
  const liveTv = useApi((signal) => getLiveTvStatus(signal).then((s) => s.configured, () => false), [])
  const [hidden, setHidden] = useState<string[]>(profile.hiddenNavigation ?? [])
  const [error, setError] = useState<string>()
  // Servers before 0.58 can't leave anything out.
  if (profile.hiddenNavigation === undefined || liveTv.loading) return null

  const has: Record<NavigationItem, boolean> = {
    watchlist: true,
    collections: true,
    playlists: libraries.some((l) => l.type === 'Music'),
    'live-tv': liveTv.data === true,
    discover: !limited,
    upcoming: !limited,
  }

  async function change(id: NavigationItem, shown: boolean) {
    const before = hidden
    const next = shown ? hidden.filter((h) => h !== id) : [...hidden, id]
    setHidden(next)
    setError(undefined)
    try {
      await setHiddenNavigation(profile.id, next as NavigationItem[])
      window.dispatchEvent(new Event(PROFILES_CHANGED))
    } catch (e) {
      setHidden(before)
      setError(errorText(e))
    }
  }

  return (
    <SettingsGroup title="Browse" note={error ? <SaveStatus error={error} /> : undefined}>
      {items
        .filter((i) => has[i.id])
        .map((i) => (
          <ToggleRow key={i.id} label={i.label} checked={!hidden.includes(i.id)} onChange={(on) => void change(i.id, on)} />
        ))}
    </SettingsGroup>
  )
}

/** Whether Podcasts has a place among the libraries in the web sidebar: a podcast setting of the profile's. */
function usePodcastsInSidebar() {
  const loaded = useApi((signal) => getPodcastPreferences(signal), [])
  const [chosen, setChosen] = useState<boolean>()
  const [error, setError] = useState<string>()
  const on = chosen ?? loaded.data?.showInSidebar

  async function change(showInSidebar: boolean) {
    setChosen(showInSidebar)
    setError(undefined)
    try {
      await savePodcastPreferences({ showInSidebar })
      window.dispatchEvent(new Event(PODCAST_PREFERENCES_CHANGED))
    } catch (e) {
      setChosen(!showInSidebar)
      setError(errorText(e))
    }
  }

  return { on, change, error }
}
