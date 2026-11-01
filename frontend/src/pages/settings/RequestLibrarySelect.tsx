import { useState } from 'react'
import { getBookRequestSetup, saveBookRequestSetup, type BookRequestSetup, type RequestDestination } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingRow, SettingsGroup, ToggleRow } from './shared'
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

/**
 * Book requests (a preview, off to start with) and how they reach Readarr, Bookshelf or Chaptarr (main profile only):
 * its profiles and the folder new books go to. Shown with such an app connected.
 */
export function BookRequestSetupGroup() {
  const loaded = useApi(getBookRequestSetup, [])
  const [setup, setSetup] = useState<BookRequestSetup>()
  const [error, setError] = useState<string>()
  const s = setup ?? loaded.data
  if (loaded.error) return <SaveStatus error={errorText(loaded.error)} />
  if (!s?.configured) return null
  const enabled = s.enabled === true

  async function save(change: Partial<Pick<BookRequestSetup, 'qualityProfileId' | 'metadataProfileId' | 'rootFolderPath' | 'enabled'>>) {
    const next = { qualityProfileId: s!.qualityProfileId, metadataProfileId: s!.metadataProfileId, rootFolderPath: s!.rootFolderPath, enabled, ...change }
    setError(undefined)
    try {
      setSetup(await saveBookRequestSetup(next))
    } catch (e) {
      setError(errorText(e))
    }
  }

  const number = (value: string) => (value ? Number(value) : null)
  return (
    <SettingsGroup title="Requests" note={error ? <SaveStatus error={error} /> : enabled && s.instance ? `Requested from ${s.instance}, for every profile.` : undefined}>
      <ToggleRow
        label="Book requests (preview)"
        help="Find and request books in Discover. Bookshelf can’t always search by title."
        checked={enabled}
        onChange={(on) => void save({ enabled: on })}
      />
      {enabled && (
        <>
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
        </>
      )}
    </SettingsGroup>
  )
}
