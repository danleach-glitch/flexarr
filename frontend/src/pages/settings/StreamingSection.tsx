import { useState } from 'react'
import type { HardwareAcceleration, Settings } from '../../api/client'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { useSettings } from './useSettings'
import { t } from '../../i18n'

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
  // What's being changed, against the settings it started from: once they're loaded again (after a save), they show.
  const [edit, setEdit] = useState<{ from: Settings['transcoding']; value: Settings['transcoding'] }>()
  const draft = edit && edit.from === settings?.transcoding ? edit.value : settings?.transcoding
  if (!settings || !draft) return waiting

  function change(next: Settings['transcoding'], now = true) {
    setEdit({ from: settings!.transcoding, value: next })
    if (now) void save({ transcoding: next })
  }

  return (
    <section>
      <SettingsHeader title={t('Streaming')}>
        {t('Files a device can play are sent as they are. The rest are converted as they play.')}
      </SettingsHeader>
      <SettingsGroup title={t('Converting video')} note={saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : 'For everyone, on every device.'}>
        <SettingRow label={t('Hardware acceleration')} help={t('Only what this server’s FFmpeg supports is listed.')}>
          <select value={draft.hardwareAcceleration} onChange={(e) => change({ ...draft, hardwareAcceleration: e.target.value as HardwareAcceleration })}>
            {settings.availableHardwareAcceleration.map((h) => (
              <option key={h} value={h}>
                {accelerationLabel[h]}
              </option>
            ))}
          </select>
        </SettingRow>
        {draft.hardwareAcceleration === 'Vaapi' && (
          <SettingRow label={t('VA-API device')}>
            <input
              type="text"
              value={draft.vaapiDevice}
              onChange={(e) => change({ ...draft, vaapiDevice: e.target.value }, false)}
              onBlur={() => draft.vaapiDevice !== settings.transcoding.vaapiDevice && change(draft)}
            />
          </SettingRow>
        )}
        {draft.hardwareAcceleration === 'None' && (
          <SettingRow label={t('Software speed')} help={t('Faster uses less of the server, at some cost to quality.')}>
            <select value={draft.softwarePreset} onChange={(e) => change({ ...draft, softwarePreset: e.target.value })}>
              <option value="ultrafast">{t('Fastest, lowest quality')}</option>
              <option value="veryfast">{t('Fast (recommended)')}</option>
              <option value="medium">{t('Slower, better quality')}</option>
            </select>
          </SettingRow>
        )}
        {draft.awayFromHome !== undefined && (
          <SettingRow label={t('Away from home')} help={t('Automatic lowers the quality only when the connection can’t keep up.')}>
            <select value={draft.awayFromHome} onChange={(e) => change({ ...draft, awayFromHome: e.target.value as 'Automatic' | 'Original' })}>
              <option value="Automatic">{t('Automatic (recommended)')}</option>
              <option value="Original">{t('Original quality')}</option>
            </select>
          </SettingRow>
        )}
        <SettingRow label={t('Limit each stream to')} help={t('Caps every stream, at home too.')}>
          <select value={draft.maxStreamingBitrate} onChange={(e) => change({ ...draft, maxStreamingBitrate: Number(e.target.value) })}>
            <option value={0}>{t('No limit')}</option>
            <option value={20_000_000}>{t('20 Mbps')}</option>
            <option value={10_000_000}>{t('10 Mbps')}</option>
            <option value={4_000_000}>{t('4 Mbps')}</option>
            <option value={2_000_000}>{t('2 Mbps')}</option>
          </select>
        </SettingRow>
      </SettingsGroup>
    </section>
  )
}
