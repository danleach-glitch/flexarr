import { getActivity, getAllLibraries, getArrStatus, getLibraryHealth, getRequestStatus, getSystemStatus } from '../../api/client'
import { ScanProgress } from '../../components/ScanProgress'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { episodeCode, formatClock } from '../../lib/format'
import { SettingLink, SettingsGroup, SettingsHeader } from './shared'
import { ServerNameRow } from './ServerNameRow'
import { locale, t, Words } from '../../i18n'

/** "Sonarr, Radarr and Lidarr" */
const list = (names: string[]) => new Intl.ListFormat(locale(), { type: 'conjunction' }).format(names)

/** How the server is doing at a glance: who's watching, what it's working on, and a line for each part of Settings. */
export function OverviewSection() {
  const activity = useApi(getActivity, [])
  usePolling(activity.reload, 5000)
  const status = useApi(getSystemStatus, []).data
  const libraries = useApi(getAllLibraries, []).data?.filter((l) => l.type !== 'Podcasts')
  const health = useApi(getLibraryHealth, []).data?.score
  const arr = useApi(getArrStatus, []).data
  const seerr = useApi(getRequestStatus, []).data
  const data = activity.data

  const titles = libraries?.reduce((n, l) => n + l.itemCount, 0) ?? 0
  const down = arr?.filter((a) => !a.ok).map((a) => a.name) ?? []
  const up = arr?.filter((a) => a.ok).map((a) => a.name) ?? []
  const connections = !arr
    ? t('Checking…')
    : arr.length === 0
      ? t('Nothing connected yet. Add Sonarr, Radarr or Lidarr for artwork and details.')
      : down.length > 0
        ? t('{apps} can’t be reached', { apps: list(down) })
        : t('{apps} connected', { apps: list([...up, ...(seerr?.reachable ? ['Seerr'] : [])]) })

  return (
    <section>
      <SettingsHeader title={t('Overview')} />

      <SettingsGroup title={t('Watching now')}>
        {!data && <p className="muted">{t('Loading…')}</p>}
        {data && data.streams.filter((s) => !s.warming).length === 0 && <p className="muted">{t('Nobody’s watching anything.')}</p>}
        {data?.streams.map((s) => (
          <div key={s.sessionId} className="setting-row static stream">
            <span className="setting-text">
              <strong>
                {s.series ?? s.item.title}
                {s.series && (
                  <span className="muted">
                    {' '}
                    {episodeCode(s.item)} {s.item.title}
                  </span>
                )}
              </strong>
              <span className="muted">
                {s.warming
                  ? t('Getting ready to play')
                  : s.paused
                    ? t('Paused at {position} of {duration}', { position: formatClock(s.positionSeconds), duration: formatClock(s.durationSeconds) })
                    : t('At {position} of {duration}', { position: formatClock(s.positionSeconds), duration: formatClock(s.durationSeconds) })}
              </span>
            </span>
            <span className="tag">{s.method === 'DirectPlay' ? t('Original file') : t('Converting to {format}', { format: s.transcode })}</span>
          </div>
        ))}
      </SettingsGroup>

      <SettingsGroup title={t('Server')}>
        <ServerNameRow />
        <SettingLink
          to="/settings/libraries"
          label={t(Words.libraries)}
          help={libraries ? `${t('{count, plural, one {# library} other {# libraries}}', { count: libraries.length })}\u2003${t('{count, plural, one {# title} other {# titles}}', { count: titles })}` : '…'}
        />
        <SettingLink
          to="/settings/health"
          label={t('Library health')}
          help={
            health
              ? `${t('Score {score}', { score: health.score })}\u2003${health.withProblems > 0 ? t('{count, plural, one {# title needs} other {# titles need}} a look', { count: health.withProblems }) : t('all in good shape')}`
              : '…'
          }
        />
        <SettingLink to="/settings/connections" label={t('Connections')} help={<span className={down.length > 0 ? 'error' : undefined}>{connections}</span>} />
        <SettingLink to="/settings/remote" label={t('Remote access')} help={status ? (status.remoteUrl ? t('Remote access is on, over Tailscale') : t('Home network only. Remote access is off')) : '…'} />
        <SettingLink to="/settings/security" label={t('Security')} help={t('The admin PIN, the API key and signed-in devices')} />
        <SettingLink to="/settings/about" label={t('About')} help={status ? `Flexarr ${status.version.split('+')[0]}` : '…'} />
      </SettingsGroup>

      {data && (
        <SettingsGroup title={t('Background work')} note={t('Episodes you start watching are analysed first. The rest happens while the server is idle.')}>
          <div className="setting-row static meter">
            {data.scan ? <ScanProgress scan={data.scan} /> : <span className="muted">{t('No scans running.')}</span>}
          </div>
          <div className="setting-row static meter">
            <span>
              {t('Intros and credits found for {done, number} of {count, plural, one {# episode} other {# episodes}}', { done: data.episodesAnalysed, count: data.episodesTotal })}
            </span>
            <span className="scan-track">
              <span style={{ width: `${data.episodesTotal ? Math.round((data.episodesAnalysed / data.episodesTotal) * 100) : 100}%` }} />
            </span>
          </div>
          <div className="setting-row static meter">
            <span>
              {t('Artwork and details for {done, number} of {count, plural, one {# title} other {# titles}}', { done: data.itemsWithMetadata, count: data.itemsTotal })}
            </span>
          </div>
        </SettingsGroup>
      )}
    </section>
  )
}
