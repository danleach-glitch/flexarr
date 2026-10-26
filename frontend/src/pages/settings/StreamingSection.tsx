import { useEffect, useState } from 'react'
import type { HardwareAcceleration, Settings } from '../../api/client'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { useSettings } from './useSettings'

const accelerationLabel: Record<HardwareAcceleration, string> = {
  Auto: 'Automatic (recommended)',
  None: 'Off: convert on the CPU',
  VideoToolbox: 'Apple VideoToolbox',
  Vaapi: 'VA-API (Intel and AMD on Linux)',
  Qsv: 'Intel Quick Sync',
  Nvenc: 'NVIDIA NVENC',
}

/** Converting video for devices that can't play the file as it is. Each choice saves as it's made. */
export function StreamingSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  const [draft, setDraft] = useState<Settings['transcoding']>()
  useEffect(() => settings && setDraft(settings.transcoding), [settings])
  if (!settings || !draft) return waiting

  function change(next: Settings['transcoding'], now = true) {
    setDraft(next)
    if (now) void save({ transcoding: next })
  }

  return (
    <section>
      <SettingsHeader title="Streaming">
        Files a device can play are sent untouched. The rest are converted as they play, on the graphics chip when the server has hardware acceleration.
      </SettingsHeader>
      <SettingsGroup title="Converting video" note={saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : 'For everyone, on every device.'}>
        <SettingRow label="Hardware acceleration" help="Only what this server’s FFmpeg supports is listed.">
          <select value={draft.hardwareAcceleration} onChange={(e) => change({ ...draft, hardwareAcceleration: e.target.value as HardwareAcceleration })}>
            {settings.availableHardwareAcceleration.map((h) => (
              <option key={h} value={h}>
                {accelerationLabel[h]}
              </option>
            ))}
          </select>
        </SettingRow>
        {draft.hardwareAcceleration === 'Vaapi' && (
          <SettingRow label="VA-API device">
            <input
              type="text"
              value={draft.vaapiDevice}
              onChange={(e) => change({ ...draft, vaapiDevice: e.target.value }, false)}
              onBlur={() => draft.vaapiDevice !== settings.transcoding.vaapiDevice && change(draft)}
            />
          </SettingRow>
        )}
        {draft.hardwareAcceleration === 'None' && (
          <SettingRow label="Software speed" help="Faster uses less of the server, at some cost to quality.">
            <select value={draft.softwarePreset} onChange={(e) => change({ ...draft, softwarePreset: e.target.value })}>
              <option value="ultrafast">Fastest, lowest quality</option>
              <option value="veryfast">Fast (recommended)</option>
              <option value="medium">Slower, better quality</option>
            </select>
          </SettingRow>
        )}
        <SettingRow label="Limit each stream to" help="Useful when watching away from home over a slow upload.">
          <select value={draft.maxStreamingBitrate} onChange={(e) => change({ ...draft, maxStreamingBitrate: Number(e.target.value) })}>
            <option value={0}>No limit</option>
            <option value={20_000_000}>20 Mbps</option>
            <option value={10_000_000}>10 Mbps</option>
            <option value={4_000_000}>4 Mbps (good for remote viewing)</option>
            <option value={2_000_000}>2 Mbps</option>
          </select>
        </SettingRow>
      </SettingsGroup>
    </section>
  )
}
