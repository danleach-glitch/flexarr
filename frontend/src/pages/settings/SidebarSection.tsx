import { useEffect, useRef, useState } from 'react'
import { clearApiCache, getPodcastPreferences, getProfiles, savePodcastPreferences, setLibraryOrder } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { useLibraries } from '../../hooks/librariesContext'
import { reorderedLibraries } from '../../lib/libraryOrder'
import { PODCAST_PREFERENCES_CHANGED } from '../../lib/podcasts'
import { RowList } from './LayoutSection'
import { SaveStatus, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

/** The profile's own: the order of its libraries in the sidebar here and in every app's menu. */
export function SidebarSection() {
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
      <SettingsHeader title="Sidebar">The order of libraries in the sidebar and the apps’ menus. Drag one to move it.</SettingsHeader>
      {shown.length > 1 ? (
        <RowList
          title={`${me.name}’s libraries`}
          rows={shown.map((l) => ({ id: l.id, label: l.name, enabled: true }))}
          onChange={(rows) => change(rows.map((r) => r.id))}
          switches={false}
        />
      ) : (
        <p className="muted">With two or more libraries, you can put them in order here.</p>
      )}
      {/* Only a failed save says anything, under the list so the list never moves. */}
      <SaveStatus error={error} />
      {libraries.some((l) => l.type === 'Podcasts') && <PodcastsInSidebar />}
    </section>
  )
}

/** Whether Podcasts has a place among the libraries: a podcast setting, kept with the profile's podcast settings. */
function PodcastsInSidebar() {
  const loaded = useApi((signal) => getPodcastPreferences(signal), [])
  const [chosen, setChosen] = useState<boolean>()
  const [error, setError] = useState<string>()
  const on = chosen ?? loaded.data?.showInSidebar
  if (on === undefined) return null

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

  return (
    <SettingsGroup title="Podcasts" note={error ? <SaveStatus error={error} /> : undefined}>
      <ToggleRow label="Show in the sidebar" help="Either way, Home has a New podcasts row." checked={on} onChange={(v) => void change(v)} />
    </SettingsGroup>
  )
}
