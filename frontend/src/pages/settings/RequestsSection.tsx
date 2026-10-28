import { useState } from 'react'
import { getProfiles, getRequestDestinations, setRequestLibraries, type Profile, type RequestDestination } from '../../api/client'
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
  // What's been chosen here since the page opened, ahead of the profiles list.
  const [changed, setMe] = useState<Profile>()
  const [error, setError] = useState<string>()
  const me = changed ?? profiles.data?.find((p) => p.isCurrent)

  const failed = profiles.error ?? destinations.error
  if (failed) return <SaveStatus error={errorText(failed)} />
  if (!me || !destinations.data) return null

  const rows = requestRows.map((r) => ({ ...r, choices: requestChoices(destinations.data!, r.mediaType, me.hiddenLibraryIds) })).filter((r) => r.choices.length > 1)

  async function change(next: Profile) {
    const before = me
    setMe(next)
    setError(undefined)
    try {
      await setRequestLibraries(next.id, { showsLibraryId: next.requestShowsLibraryId ?? null, filmsLibraryId: next.requestFilmsLibraryId ?? null })
    } catch (e) {
      setMe(before)
      setError(errorText(e))
    }
  }

  return (
    <section>
      <SettingsHeader title="Requests">Where the shows and films you request from Discover end up.</SettingsHeader>
      <SettingsGroup title="Where requests go" note={error ? <SaveStatus error={error} /> : `${me.name}’s own. Requests from every device follow it.`}>
        {rows.length ? (
          rows.map((r) => (
            <SettingRow key={r.key} label={r.label} help={r.help}>
              <RequestLibrarySelect choices={r.choices} value={me[r.key]} onChange={(id) => void change({ ...me, [r.key]: id })} />
            </SettingRow>
          ))
        ) : (
          <SettingRow as="div" label="Nothing to choose" help="Requests go to the one library Seerr adds each kind to. With two TV or film libraries Seerr can add to, you choose here." />
        )}
      </SettingsGroup>
    </section>
  )
}
