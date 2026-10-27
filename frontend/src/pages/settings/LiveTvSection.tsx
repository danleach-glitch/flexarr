import { useState } from 'react'
import type { LiveTvSettingsUpdate } from '../../api/client'
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
    </section>
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
