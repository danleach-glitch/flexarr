import { useDeferredValue, useState } from 'react'
import { getLiveChannels, setChannelFineForKids, type LiveChannel, type LiveTvSettingsUpdate } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { useSession } from '../../hooks/sessionContext'
import { errorText } from './useSettings'
import { SaveStatus, SettingLink, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { useSettings } from './useSettings'

/** A select's choices with the saved value among them (set elsewhere, it would otherwise be changed by saving). */
function withSaved(choices: number[], saved: number | undefined) {
  return saved === undefined || choices.includes(saved) ? choices : [...choices, saved].sort((a, b) => a - b)
}

/** How Live TV behaves: rewinding a channel, and the DVR. Dispatcharr itself is set up in Connections. Saves as it changes. */
export function LiveTvSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  if (!settings) return waiting
  const liveTv = settings.liveTv

  const header = (
    <SettingsHeader title="Live TV">Watching channels from Dispatcharr, and recording them.</SettingsHeader>
  )
  if (!liveTv) {
    return (
      <section>
        {header}
        <SettingsGroup>
          <SettingLink to="/settings/connections" label="Set up Dispatcharr" help="Live TV’s channels and guide come from Dispatcharr, in Connections." />
        </SettingsGroup>
      </section>
    )
  }

  // The source stays as it is (an omitted key keeps the saved one); only the setting changed goes with it.
  const change = (setting: Omit<LiveTvSettingsUpdate, 'baseUrl' | 'username' | 'password'>) => void save({ liveTv: { baseUrl: liveTv.baseUrl, username: liveTv.username, ...setting } })
  const status = saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : undefined

  return (
    <section>
      {header}
      <SettingsGroup title="Watching" note={status}>
        <SettingRow label="Rewind up to" help="Kept on this server while someone watches a channel, about 2 GB an hour for HD.">
          <select value={liveTv.rewindMinutes} onChange={(e) => change({ rewindMinutes: Number(e.target.value) })}>
            {withSaved([15, 30, 60, 120, 240], liveTv.rewindMinutes).map((m) => (
              <option key={m} value={m}>
                {m < 60 ? `${m} minutes` : m === 60 ? '1 hour' : `${m / 60} hours`}
              </option>
            ))}
          </select>
        </SettingRow>
        <SettingRow label="Channels at once" help="Recordings and live viewing share what your IPTV login allows. Watching and recording the same channel counts once.">
          <select value={liveTv.maxStreams ?? 0} onChange={(e) => change({ maxStreams: Number(e.target.value) })}>
            <option value={0}>As Dispatcharr says</option>
            {withSaved([1, 2, 3, 4, 5], liveTv.maxStreams ?? undefined).map((n) => (
              <option key={n} value={n}>
                {n}
              </option>
            ))}
          </select>
        </SettingRow>
      </SettingsGroup>
      <SettingsGroup title="Recordings" note="Broadcasts drift, so recordings start a little early and run on.">
        <FolderRow key={liveTv.dvrFolder ?? ''} saved={liveTv.dvrFolder ?? ''} inUse={liveTv.dvrFolderInUse} onSave={(dvrFolder) => change({ dvrFolder })} />
        <SettingRow label="Start early">
          <select value={liveTv.paddingBeforeMinutes} onChange={(e) => change({ paddingBeforeMinutes: Number(e.target.value) })}>
            {withSaved([0, 1, 2, 5, 10], liveTv.paddingBeforeMinutes).map((m) => (
              <option key={m} value={m}>
                {m === 0 ? 'On time' : `${m} min`}
              </option>
            ))}
          </select>
        </SettingRow>
        <SettingRow label="Run on">
          <select value={liveTv.paddingAfterMinutes} onChange={(e) => change({ paddingAfterMinutes: Number(e.target.value) })}>
            {withSaved([0, 2, 5, 10, 15, 30], liveTv.paddingAfterMinutes).map((m) => (
              <option key={m} value={m}>
                {m === 0 ? 'No' : `${m} min`}
              </option>
            ))}
          </select>
        </SettingRow>
        <SettingRow label="Keep free on the drive" help="No new recording starts when there’s less than this.">
          <select value={liveTv.minFreeGigabytes} onChange={(e) => change({ minFreeGigabytes: Number(e.target.value) })}>
            {withSaved([10, 25, 50, 100, 250, 500], liveTv.minFreeGigabytes).map((g) => (
              <option key={g} value={g}>
                {g} GB
              </option>
            ))}
          </select>
        </SettingRow>
      </SettingsGroup>
      <KidsChannels />
    </section>
  )
}

/** How many channels Channels for kids lists at once. */
const ShownChannels = 100

/**
 * The channels profiles with an age limit may watch, once a profile has one. Guide ratings are too patchy to go by,
 * so a parent picks; none picked, those profiles don't see Live TV.
 */
function KidsChannels() {
  const limits = useSession().profiles?.some((p) => p.maxRatingAge != null) === true
  const channels = useApi((signal) => (limits ? getLiveChannels(signal) : Promise.resolve([] as LiveChannel[])), [limits]).data
  const [fine, setFine] = useState<Record<string, boolean>>({})
  const [filter, setFilter] = useState('')
  // Typing stays quick with thousands of channels: the list follows a moment behind.
  const deferred = useDeferredValue(filter)
  const [error, setError] = useState<string>()
  if (!limits || !channels?.length || channels[0].fineForKids === undefined) return null

  const isFine = (c: LiveChannel) => fine[c.id] ?? c.fineForKids === true
  const matching = channels.filter((c) => c.name.toLowerCase().includes(deferred.trim().toLowerCase()))
  // The picked ones first, then the first of the rest: finding the others is what the search is for.
  const shown = [...matching.filter(isFine), ...matching.filter((c) => !isFine(c))].slice(0, ShownChannels)
  async function toggle(channel: LiveChannel, to: boolean) {
    setFine((f) => ({ ...f, [channel.id]: to }))
    setError(undefined)
    try {
      await setChannelFineForKids(channel.id, to)
    } catch (e) {
      setFine((f) => ({ ...f, [channel.id]: !to }))
      setError(errorText(e))
    }
  }

  return (
    <SettingsGroup
      title="Channels for kids"
      note={error ? <SaveStatus error={error} /> : `${channels.filter(isFine).length} of ${channels.length} for profiles with an age limit.`}
    >
      <SettingRow label="Find a channel">
        <input type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Name" />
      </SettingRow>
      <div className="kids-channels">
        {shown.map((c) => (
          <SettingRow key={c.id} label={c.name} help={c.group ?? undefined}>
            <input type="checkbox" role="switch" className="switch" checked={isFine(c)} onChange={(e) => void toggle(c, e.target.checked)} />
          </SettingRow>
        ))}
        {matching.length === 0 && <SettingRow label="No channels by that name" />}
        {matching.length > shown.length && <SettingRow label={`${matching.length - shown.length} more`} help="Search to find them." />}
      </div>
    </SettingsGroup>
  )
}

/** Where recordings go: saved when the field is left, if it changed. */
function FolderRow({ saved, inUse, onSave }: { saved: string; inUse: string; onSave: (folder: string) => void }) {
  const [folder, setFolder] = useState(saved)
  return (
    <SettingRow label="Folder" help={folder.trim() ? 'Recordings made from now on go here. Earlier ones stay where they are.' : `Empty for the default${inUse ? `: ${inUse}` : ''}.`}>
      <input
        type="text"
        value={folder}
        placeholder={inUse || 'A DVR folder on the drive your TV library is on'}
        onChange={(e) => setFolder(e.target.value)}
        onBlur={() => folder.trim() !== saved && onSave(folder.trim())}
      />
    </SettingRow>
  )
}
