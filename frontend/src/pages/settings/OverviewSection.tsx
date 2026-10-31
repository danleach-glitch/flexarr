import { getActivity, getAllLibraries, getArrStatus, getLibraryHealth, getRequestStatus, getSystemStatus } from '../../api/client'
import { ScanProgress } from '../../components/ScanProgress'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { episodeCode, formatClock } from '../../lib/format'
import { SettingLink, SettingsGroup, SettingsHeader } from './shared'

/** "Sonarr, Radarr and Lidarr" */
const list = (names: string[]) => (names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`)

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
    ? 'Checking…'
    : arr.length === 0
      ? 'Nothing connected yet. Add Sonarr, Radarr or Lidarr for artwork and details.'
      : down.length > 0
        ? `${list(down)} can’t be reached`
        : `${list([...up, ...(seerr?.reachable ? ['Seerr'] : [])])} connected`

  return (
    <section>
      <SettingsHeader title="Overview" />

      <SettingsGroup title="Watching now">
        {!data && <p className="muted">Loading…</p>}
        {data?.streams.length === 0 && <p className="muted">Nobody’s watching anything.</p>}
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
                {s.paused ? 'Paused at' : 'At'} {formatClock(s.positionSeconds)} of {formatClock(s.durationSeconds)}
              </span>
            </span>
            <span className="tag">{s.method === 'DirectPlay' ? 'Original file' : `Converting to ${s.transcode}`}</span>
          </div>
        ))}
      </SettingsGroup>

      <SettingsGroup title="Server">
        <SettingLink
          to="/settings/libraries"
          label="Libraries"
          help={libraries ? `${libraries.length.toLocaleString()} ${libraries.length === 1 ? 'library' : 'libraries'} · ${titles.toLocaleString()} titles` : '…'}
        />
        <SettingLink
          to="/settings/health"
          label="Library health"
          help={
            health
              ? `Score ${health.score}${health.withProblems > 0 ? ` · ${health.withProblems.toLocaleString()} ${health.withProblems === 1 ? 'title needs' : 'titles need'} a look` : ' · all in good shape'}`
              : '…'
          }
        />
        <SettingLink to="/settings/connections" label="Connections" help={<span className={down.length > 0 ? 'error' : undefined}>{connections}</span>} />
        <SettingLink to="/settings/access" label="Access" help={status ? (status.remoteUrl ? 'Remote access is on, over Tailscale' : 'Home network only. Remote access is off') : '…'} />
        <SettingLink to="/settings/security" label="Security" help="The admin PIN, the API key and signed-in devices" />
        <SettingLink to="/settings/about" label="About" help={status ? `Flexarr ${status.version.split('+')[0]}` : '…'} />
      </SettingsGroup>

      {data && (
        <SettingsGroup title="Background work" note="Episodes you start watching are analysed first. The rest happens while the server is idle.">
          <div className="setting-row static meter">
            {data.scan ? <ScanProgress scan={data.scan} /> : <span className="muted">No scans running.</span>}
          </div>
          <div className="setting-row static meter">
            <span>
              Intros and credits found for {data.episodesAnalysed.toLocaleString()} of {data.episodesTotal.toLocaleString()} episodes
            </span>
            <span className="scan-track">
              <span style={{ width: `${data.episodesTotal ? Math.round((data.episodesAnalysed / data.episodesTotal) * 100) : 100}%` }} />
            </span>
          </div>
          <div className="setting-row static meter">
            <span>
              Artwork and details for {data.itemsWithMetadata.toLocaleString()} of {data.itemsTotal.toLocaleString()} titles
            </span>
          </div>
        </SettingsGroup>
      )}
    </section>
  )
}
