import { useState } from 'react'
import {
  getBookRequestSetup,
  getProfiles,
  getRequestDestinations,
  getRequestStatus,
  saveBookRequestSetup,
  setBookRequestLibrary,
  setRequestLibraries,
  type BookRequestSetup,
  type Profile,
  type RequestDestination,
} from '../../api/client'
import { useLibraries } from '../../hooks/librariesContext'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { requestChoices, requestRows } from '../../lib/requestLibraries'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText } from './useSettings'

/**
 * Which library a kind of request goes to. Seerr's default library stands for "not chosen" (null), so a profile that
 * never chose follows Seerr if its default changes; a choice that's since been hidden or gone shows as the default too.
 */
export function RequestLibrarySelect({
  choices,
  value,
  onChange,
  label,
}: {
  choices: RequestDestination[]
  value: string | null | undefined
  onChange: (libraryId: string | null) => void
  label?: string
}) {
  const fallback = choices.find((d) => d.isDefault)
  const shown = choices.some((d) => d.id === value) ? value! : (fallback?.id ?? '')
  return (
    <select aria-label={label} value={shown} onChange={(e) => onChange(e.target.value === '' || e.target.value === fallback?.id ? null : e.target.value)}>
      {!fallback && <option value="">Wherever Seerr adds them</option>}
      {choices.map((d) => (
        <option key={d.id} value={d.id}>
          {d.name}
        </option>
      ))}
    </select>
  )
}

/** The profile's own: which library the shows and films it requests are added to, on every device. */
export function RequestsSection() {
  const profiles = useApi(getProfiles, [])
  const destinations = useApi(getRequestDestinations, [])
  const status = useApi((signal) => getRequestStatus(signal).catch(() => undefined), [])
  const { libraries } = useLibraries()
  const isAdmin = useSession().me?.isAdmin === true
  // What's been chosen here since the page opened, ahead of the profiles list.
  const [changed, setMe] = useState<Profile>()
  const [error, setError] = useState<string>()
  const me = changed ?? profiles.data?.find((p) => p.isCurrent)

  const failed = profiles.error ?? destinations.error
  if (failed) return <SaveStatus error={errorText(failed)} />
  if (!me || !destinations.data) return null

  const books = status.data?.books === true
  const bookLibraries = libraries.filter((l) => l.type === 'Audiobooks')
  const rows = requestRows.map((r) => ({ ...r, choices: requestChoices(destinations.data!, r.mediaType, me.hiddenLibraryIds) })).filter((r) => r.choices.length > 1)

  async function change(next: Profile) {
    const before = me
    setMe(next)
    setError(undefined)
    try {
      if (next.requestAudiobooksLibraryId !== me!.requestAudiobooksLibraryId) await setBookRequestLibrary(next.id, next.requestAudiobooksLibraryId ?? null)
      else await setRequestLibraries(next.id, { showsLibraryId: next.requestShowsLibraryId ?? null, filmsLibraryId: next.requestFilmsLibraryId ?? null })
    } catch (e) {
      setMe(before)
      setError(errorText(e))
    }
  }

  return (
    <section>
      <SettingsHeader title="Requests">Where what you request from Discover ends up.</SettingsHeader>
      <SettingsGroup title="Where requests go" note={error ? <SaveStatus error={error} /> : `${me.name}’s own. Requests from every device follow it.`}>
        {books && bookLibraries.length > 1 && (
          <SettingRow label="New books go to" help="The library a book is added to when you request it.">
            <select value={me.requestAudiobooksLibraryId ?? ''} onChange={(e) => void change({ ...me, requestAudiobooksLibraryId: e.target.value || null })}>
              <option value="">As requests are set up</option>
              {bookLibraries.map((l) => (
                <option key={l.id} value={l.id}>
                  {l.name}
                </option>
              ))}
            </select>
          </SettingRow>
        )}
        {rows.length || (books && bookLibraries.length > 1) ? (
          rows.map((r) => (
            <SettingRow key={r.key} label={r.label} help={r.help}>
              <RequestLibrarySelect choices={r.choices} value={me[r.key]} onChange={(id) => void change({ ...me, [r.key]: id })} />
            </SettingRow>
          ))
        ) : (
          <SettingRow as="div" label="Nothing to choose" help="Requests go to the one library Seerr adds each kind to. With two TV or film libraries Seerr can add to, you choose here." />
        )}
      </SettingsGroup>
      {books && isAdmin && <BookRequestSetupGroup />}
    </section>
  )
}

/** How book requests reach Readarr, Bookshelf or Chaptarr (main profile only): its profiles and the folder new books go to. */
function BookRequestSetupGroup() {
  const loaded = useApi(getBookRequestSetup, [])
  const [setup, setSetup] = useState<BookRequestSetup>()
  const [error, setError] = useState<string>()
  const s = setup ?? loaded.data
  if (loaded.error) return <SaveStatus error={errorText(loaded.error)} />
  if (!s) return null

  async function save(change: Partial<Pick<BookRequestSetup, 'qualityProfileId' | 'metadataProfileId' | 'rootFolderPath'>>) {
    const next = { qualityProfileId: s!.qualityProfileId, metadataProfileId: s!.metadataProfileId, rootFolderPath: s!.rootFolderPath, ...change }
    setError(undefined)
    try {
      setSetup(await saveBookRequestSetup(next))
    } catch (e) {
      setError(errorText(e))
    }
  }

  const number = (value: string) => (value ? Number(value) : null)
  return (
    <SettingsGroup title="Audiobooks" note={error ? <SaveStatus error={error} /> : s.instance ? `Requested from ${s.instance}, for every profile.` : undefined}>
      <SettingRow label="Quality">
        <select value={s.qualityProfileId ?? ''} onChange={(e) => void save({ qualityProfileId: number(e.target.value) })}>
          <option value="">The app’s first</option>
          {s.qualityProfiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
      </SettingRow>
      <SettingRow label="Metadata">
        <select value={s.metadataProfileId ?? ''} onChange={(e) => void save({ metadataProfileId: number(e.target.value) })}>
          <option value="">The app’s first</option>
          {s.metadataProfiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
      </SettingRow>
      <SettingRow label="Folder" help="Where new books go, unless a profile chooses its library.">
        <select value={s.rootFolderPath ?? ''} onChange={(e) => void save({ rootFolderPath: e.target.value || null })}>
          <option value="">The app’s first</option>
          {s.rootFolders.map((f) => (
            <option key={f.path} value={f.path}>
              {f.path}
            </option>
          ))}
        </select>
      </SettingRow>
    </SettingsGroup>
  )
}
