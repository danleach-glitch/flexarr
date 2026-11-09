import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError, getLibraryHealth, confirmHeldRemoval, keepMatch, removeDuplicates, deleteCopy, getHealthCheck, startHealthCheck, type ItemSummary, type LibraryHealth, type HeldFolder, searchAgain, mergeAlbums, mergeArtists } from '../../api/client'
import { Artwork } from '../../components/Artwork'
import { AsyncButton } from '../../components/AsyncButton'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { FixMatchDialog } from '../../components/FixMatchDialog'
import { MusicMatchById } from '../../components/MusicMatchDialog'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { episodeCode, formatSize, relativeTime, unreadableReason } from '../../lib/format'
import { SettingsHeader } from './shared'
import { locale, t, tk, Words } from '../../i18n'

function HealthCheckPanel({ onFinished }: { onFinished: () => void }) {
  const check = useApi(getHealthCheck, [])
  const [requested, setRequested] = useState<Record<string, string>>({})
  const running = check.data?.running ?? false

  // Poll while a check runs; when it finishes, the rest of the report reloads too.
  usePolling(check.reload, running ? 1500 : null)
  const wasRunning = useRef(false)
  useEffect(() => {
    if (running) wasRunning.current = true
    else if (wasRunning.current) {
      wasRunning.current = false
      onFinished()
    }
  }, [running, onFinished])

  const report = check.data
  const labels = { Missing: t('Missing from disk'), Empty: t('Empty files'), Unreadable: t('Still unreadable') }
  return (
    <div className="panel health-check">
      <div className="health-check-head">
        <div>
          <strong>{t('Health check')}</strong>
          <p className="muted">
            {running && report
              ? t('Checking files: {checked, number} of {total, number}', { checked: report.checked, total: report.total })
              : report?.finishedAt
                ? report.problems.length
                  ? t('Last checked {time}: {total, plural, one {# file} other {# files}}, {problems, number} with problems.', { time: relativeTime(report.finishedAt), total: report.total, problems: report.problems.length })
                  : t('Last checked {time}: {total, plural, one {# file} other {# files}}, no problems.', { time: relativeTime(report.finishedAt), total: report.total })
                : t('Looks at every file: whether it’s still on disk, not empty, and readable.')}
          </p>
        </div>
        <AsyncButton
          className="button quiet small"
          busy={running}
          onClick={async () => {
            await startHealthCheck()
            check.reload()
          }}
        >
          {t('Run health check')}
        </AsyncButton>
      </div>
      {running && report && report.total > 0 && (
        <span className="scan-track" aria-hidden="true">
          <span style={{ width: `${(report.checked / report.total) * 100}%` }} />
        </span>
      )}
      {!running &&
        (['Missing', 'Empty', 'Unreadable'] as const).map((kind) => {
          const found = report?.problems.filter((p) => p.file.problem === kind) ?? []
          if (found.length === 0) return null
          return (
            <div key={kind} className="health-check-group">
              <h4>
                {labels[kind]} ({found.length.toLocaleString(locale())})
              </h4>
              <ul className="health-check-list">
                {found.slice(0, 100).map(({ file, item }) => (
                  <li key={file.fileId}>
                    <span>
                      <strong>{item ? (item.seriesTitle ? `${item.seriesTitle} ${episodeCode(item)}` : item.title) : t('Unknown title')}</strong>
                      <code>{file.path}</code>
                    </span>
                    {item && (
                      <AsyncButton
                        className="button quiet small"
                        disabled={!!requested[item.id]}
                        onClick={async () => {
                          try {
                            await searchAgain(item.id)
                            setRequested((r) => ({ ...r, [item.id]: t('Search started') }))
                          } catch (e) {
                            setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('Search failed') }))
                          }
                        }}
                      >
                        {requested[item.id] ?? t('Search again')}
                      </AsyncButton>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          )
        })}
    </div>
  )
}

export function HealthSection() {
  const { data, reload } = useApi(getLibraryHealth, [])
  const [requested, setRequested] = useState<Record<string, string>>({})
  const [shown, setShown] = useState(40)
  const [removing, setRemoving] = useState<{ done: number; total: number; freed: number; kept: number; failed: number } | null>(null)
  const [copyErrors, setCopyErrors] = useState<Record<string, string>>({})
  const [confirmAll, setConfirmAll] = useState(false)
  const [matching, setMatching] = useState<ItemSummary>()
  if (!data) return <p className="muted">{t('Checking your library…')}</p>

  async function upgrade(id: string) {
    try {
      await searchAgain(id)
      setRequested((r) => ({ ...r, [id]: t('Search started') }))
    } catch (e) {
      setRequested((r) => ({ ...r, [id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('Search failed') }))
    }
  }

  /** One title at a time, so progress is visible and one failure doesn't stop the rest. */
  async function removeSpares(ids: string[]) {
    let freed = 0
    let kept = 0
    let failed = 0
    setRemoving({ done: 0, total: ids.length, freed, kept, failed })
    for (const [i, id] of ids.entries()) {
      try {
        const result = await removeDuplicates(id)
        freed += result.freedBytes
        // Nothing removed: Sonarr or Radarr use a different copy from the best one, so both stay.
        if (result.removed === 0) kept++
        failed += result.skipped.length
      } catch {
        failed++
      }
      setRemoving({ done: i + 1, total: ids.length, freed, kept, failed })
    }
    reload()
  }

  async function removeCopy(fileId: string) {
    try {
      await deleteCopy(fileId)
      reload()
    } catch (e) {
      setCopyErrors((c) => ({ ...c, [fileId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('Couldn’t delete it') }))
    }
  }

  const score = data.score
  // Without artwork, as the Missing artwork list has them: those not found, and those no Radarr or Sonarr is connected
  // to fill in (which the score leaves out).
  const needsConnection = score.artworkNeedsConnection ?? 0
  const withoutArtwork = score.missingArtwork + needsConnection
  // Albums count as titles on servers that check music.
  const music = score.albumsWithoutCover === undefined ? 'no' : 'yes'
  const breakdown = [
    [score.duplicates, tk('{count} with spare copies')],
    [score.unreadable, tk('{count} unreadable')],
    [score.fileProblems, tk('{count} missing or empty files')],
    [score.standardDefinition, tk('{count} only in SD')],
    [score.albumsWithoutCover ?? 0, tk('{count} albums without a cover')],
    [score.duplicateAlbums ?? 0, tk('{count} albums twice')],
    [score.albumsWithRepeatedTracks ?? 0, tk('{count} albums with tracks twice')],
    [score.splitArtists ?? 0, tk('{count} artists under two names')],
  ] as const

  // Groups with nothing in them drop out, so what's left is what needs a look; this line says what's fine.
  const clear = [
    data.duplicates.length === 0 && t('no spare copies'),
    data.unreadable.length === 0 && t('every file opened cleanly'),
    data.standardDefinitionTotal === 0 && t('every movie has an HD copy'),
  ].filter((s): s is string => !!s)

  return (
    <section className="health">
      <SettingsHeader title={t('Library health')} back={{ to: '/settings/libraries', label: t(Words.libraries) }} />

      <div className="panel health-score" data-grade={score.score >= 95 ? 'good' : score.score >= 80 ? 'fair' : 'poor'}>
        <div className="health-score-number">
          <span className="health-score-value">{score.score}</span>
          <span className="health-score-label">{t('Health score')}</span>
        </div>
        <div>
          <p className="health-score-summary">
            {score.withProblems === 0
              ? t('{music, select, yes {All {count, number} movies, episodes and albums are in good shape.} other {All {count, number} movies and episodes are in good shape.}}', { music, count: score.titles })
              : t('{music, select, yes {{fine, number} of {count, number} movies, episodes and albums have no problems.} other {{fine, number} of {count, number} movies and episodes have no problems.}}', { music, fine: score.titles - score.withProblems, count: score.titles })}
          </p>
          <ul className="health-score-breakdown">
            {breakdown
              .filter(([n]) => n > 0)
              .map(([n, label]) => (
                <li key={label}>
                  {t.rich(label, { count: <strong>{n.toLocaleString(locale())}</strong> })}
                </li>
              ))}
            {data.held.length > 0 && (
              <li>
                {t.rich('{count, plural, one {{n} folder on hold} other {{n} folders on hold}}', { count: data.held.length, n: <strong>{data.held.length.toLocaleString(locale())}</strong> })}
              </li>
            )}
            {withoutArtwork > 0 && (
              <li>
                {needsConnection === withoutArtwork
                  ? t.rich('{count} without artwork: {connect} to fill it in', { count: <strong>{withoutArtwork.toLocaleString(locale())}</strong>, connect: <Link to="/settings/connections">{t('connect Radarr or Sonarr')}</Link> })
                  : needsConnection > 0
                    ? t.rich('{count} without artwork ({connect} to fill in {needs})', {
                        count: <strong>{withoutArtwork.toLocaleString(locale())}</strong>,
                        connect: <Link to="/settings/connections">{t('connect Radarr or Sonarr')}</Link>,
                        needs: needsConnection.toLocaleString(locale()),
                      })
                    : t.rich('{count} without artwork', { count: <strong>{withoutArtwork.toLocaleString(locale())}</strong> })}
              </li>
            )}
          </ul>
        </div>
      </div>

      <HealthCheckPanel onFinished={reload} />

      {data.held.length > 0 && <HeldFolders folders={data.held} onConfirmed={reload} />}

      {clear.length > 0 && <p className="muted health-clear">{t('All clear: {list}.', { list: clear.join(', ') })}</p>}

      <HealthGroup
        title={t('Spare copies')}
        count={data.duplicates.length}
        // Stays while spares are removed, so the last of them going doesn't take the progress with it.
        shown={removing !== null}
        lede={t('More than one file alike for the same movie or episode. Removing spares frees {size}; the best copy, and the one Sonarr or Radarr use, always stay.', { size: formatSize(data.reclaimableBytes) })}
        action={
          data.duplicates.length > 0 && (
            <AsyncButton className="button danger small" busy={removing !== null && removing.done < removing.total} onClick={() => setConfirmAll(true)}>
              {t('Remove all spare copies ({size})', { size: formatSize(data.reclaimableBytes) })}
            </AsyncButton>
          )
        }
      >
        {removing && (
          <div className="health-progress" role="status">
            <span>
              {removing.done < removing.total
                ? t('Removing spare copies: {done, number} of {total, plural, one {# title} other {# titles}}', { done: removing.done, total: removing.total })
                : removing.total - removing.kept - removing.failed > 0
                  ? t('Removed spare copies of {count, plural, one {# title} other {# titles}}, freeing {size}.', { count: removing.total - removing.kept - removing.failed, size: formatSize(removing.freed) })
                  : t('Nothing was removed.')}
              {removing.done === removing.total &&
                removing.kept > 0 &&
                ` ${t('{count, number} kept every copy: Sonarr or Radarr use a different copy from the best one. Open a title to delete the copy you don’t want.', { count: removing.kept })}`}
              {removing.failed > 0 && ` ${t('{count, number} couldn’t be removed (see the server log).', { count: removing.failed })}`}
            </span>
            <span className="scan-track" aria-hidden="true">
              <span style={{ width: `${(removing.done / Math.max(1, removing.total)) * 100}%` }} />
            </span>
          </div>
        )}
        {data.duplicates.slice(0, shown).map((d) => (
          <details key={d.item.id} className="health-row-expandable">
            <summary>
              <HealthRowMain item={d.item} detail={t('{count, plural, one {# copy} other {# copies}}, {size} spare', { count: d.files.length, size: formatSize(d.reclaimableBytes) })} />
              <AsyncButton
                className="button quiet small"
                onClick={(e) => {
                  e.preventDefault()
                  return removeSpares([d.item.id])
                }}
              >
                {t('Remove spares')}
              </AsyncButton>
            </summary>
            <ul className="health-copies">
              {d.files.map((f) => {
                // Copies identical to the best: which one stays is whichever Sonarr or Radarr use.
                const best = d.files.find((x) => x.keep)!
                const same = (x: typeof f) => x.size === best.size && x.resolution === best.resolution
                const identical = same(f) && d.files.filter(same).length > 1
                return (
                  <li key={f.id}>
                    <span className={f.keep || identical ? 'tag keep' : 'tag'}>{identical ? t('Identical') : f.keep ? t('Best') : t('Spare')}</span>
                    <span title={f.path}>{fileName(f.path)}</span>
                    <span className="muted">{[f.resolution, f.videoCodec?.toUpperCase(), formatSize(f.size)].filter(Boolean).join(', ')}</span>
                    <AsyncButton className="text-button" onClick={() => removeCopy(f.id)}>
                      {t('Delete this copy')}
                    </AsyncButton>
                    {copyErrors[f.id] && (
                      <span className="error" role="alert">
                        {copyErrors[f.id]}
                      </span>
                    )}
                  </li>
                )
              })}
            </ul>
          </details>
        ))}
        {data.duplicates.length > shown && (
          <button className="button quiet small health-more" onClick={() => setShown((n) => n + 100)}>
            {t('Show more ({count, number} left)', { count: data.duplicates.length - shown })}
          </button>
        )}
      </HealthGroup>

      <HealthGroup
        title={t('Versions')}
        count={data.versionsTotal ?? 0}
        lede={t('Titles you have in more than one version, 4K and 1080p say, which is usually on purpose. Together they take {size}.', { size: formatSize(data.versionsBytes ?? 0) })}
      >
        {(data.versions ?? []).map((v) => (
          <details key={v.item.id} className="health-row-expandable">
            <summary>
              <HealthRowMain item={v.item} detail={t('{list}, {size}', { list: [...new Set(v.files.map((f) => f.label).filter(Boolean))].join(', '), size: formatSize(v.bytes) })} />
            </summary>
            <ul className="health-copies">
              {v.files.map((f) => (
                <li key={f.id}>
                  <span title={f.path}>{fileName(f.path)}</span>
                  <span className="muted">{[f.label, f.edition, formatSize(f.size)].filter(Boolean).join(', ')}</span>
                </li>
              ))}
            </ul>
          </details>
        ))}
      </HealthGroup>

      <HealthGroup title={t('Files that can’t be read')} count={data.unreadable.length} lede={t('Usually a damaged or unfinished download. A new copy fixes playback.')}>
        {data.unreadable.map((u) => (
          <div key={u.fileId} className="health-row">
            <HealthRowMain item={u.item} file={u.path} detail={t('Can’t be read: {reason}', { reason: unreadableReason(u.error) })} />
            {u.item &&
              searchable.has(u.item.kind) &&
              (data.downloading[u.item.id] ? (
                <DownloadingNote state={data.downloading[u.item.id]} />
              ) : (
                <AsyncButton className="button quiet small" onClick={() => upgrade(u.item!.id)} disabled={!!requested[u.item.id]}>
                  {requested[u.item.id] ?? t('Get a new copy')}
                </AsyncButton>
              ))}
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title={t('Only in standard definition')} count={data.standardDefinitionTotal} lede={t('Your best copy is below 720p. Radarr can look for a better one.')}>
        {data.standardDefinition.map((item) => (
          <div key={item.id} className="health-row">
            <HealthRowMain item={item} detail={t('Best copy is SD')} />
            {data.downloading[item.id] ? (
              <DownloadingNote state={data.downloading[item.id]} />
            ) : (
              <AsyncButton className="button quiet small" onClick={() => upgrade(item.id)} disabled={!!requested[item.id]}>
                {requested[item.id] ?? t('Find a better copy')}
              </AsyncButton>
            )}
          </div>
        ))}
      </HealthGroup>

      {data.matchedByTitle.length > 0 && (
        <HealthGroup
          title={t('Check these matches')}
          count={data.matchedByTitle.length}
          lede={t('Sonarr and Radarr don’t manage these, so their details came from searching for the folder’s name, which can find the wrong one: an older show with the same name, or a remake. Fixing a match adds the title to Sonarr or Radarr. Ignore one that’s right and it stays as it is.')}
        >
          {data.matchedByTitle.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail={requested[item.id] ?? t('Matched by title')} />
              <div className="health-row-actions">
                <AsyncButton
                  className="button quiet small"
                  disabled={requested[item.id] === t('Keeping this match…')}
                  onClick={async () => {
                    setRequested((r) => ({ ...r, [item.id]: t('Keeping this match…') }))
                    try {
                      await keepMatch(item.id)
                      reload()
                    } catch (e) {
                      setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('Couldn’t keep this match') }))
                    }
                  }}
                >
                  {t('Ignore')}
                </AsyncButton>
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  {t('Fix match')}
                </button>
              </div>
            </div>
          ))}
        </HealthGroup>
      )}

      {data.missingArtwork.length > 0 && (
        <HealthGroup
          title={t('Missing artwork')}
          count={data.missingArtwork.length}
          lede={
            score.missingArtwork === 0 && needsConnection > 0
              ? t('Artwork comes from Radarr and Sonarr. Connect them in Settings › Connections to fill it in.')
              : needsConnection > 0
                ? t('{count, plural, one {# needs} other {# need}} Radarr or Sonarr connected in Settings › Connections to fill it in. The rest aren’t found there: fix the match to choose the right title, or rename the folder to “Title (Year)”.', { count: needsConnection })
                : t('Not found in Sonarr or Radarr. Fix the match to choose the right title, or rename the folder to “Title (Year)”.')
          }
        >
          {data.missingArtwork.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail={t('No poster or details')} />
              {(item.kind === 'Movie' || item.kind === 'Series') && (
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  {t('Fix match')}
                </button>
              )}
            </div>
          ))}
        </HealthGroup>
      )}

      <MusicHealth data={data} onChanged={reload} />

      {matching && <FixMatchDialog item={matching} onFixed={reload} onClose={() => setMatching(undefined)} />}

      {confirmAll && (
        <ConfirmDialog
          title={t('{count, plural, one {Remove # spare copy?} other {Remove # spare copies?}}', { count: data.duplicates.length })}
          cancelLabel={t('Keep them')}
          confirmLabel={t('Remove spare copies')}
          danger
          onClose={() => setConfirmAll(false)}
          onConfirm={() => {
            setConfirmAll(false)
            void removeSpares(data.duplicates.map((d) => d.item.id))
          }}
        >
          <p>
            {t('This deletes the spare files from disk, freeing {size}. For each title the best copy, and the one Sonarr or Radarr use, are kept. You can’t undo this.', { size: formatSize(data.reclaimableBytes) })}
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

const fileName = (path: string) => path.split('/').at(-1) ?? path

/** What Sonarr and Radarr can search for again: an audiobook, a song or a podcast can't be fetched anew from here. */
const searchable = new Set<ItemSummary['kind']>(['Movie', 'Series', 'Season', 'Episode'])

/**
 * Folders whose files the scans are keeping: they looked unmounted or cleared out. If they really were cleared out,
 * confirming lets the next scan hide them (and remove them a day later).
 */
function HeldFolders({ folders, onConfirmed }: { folders: HeldFolder[]; onConfirmed: () => void }) {
  const [asking, setAsking] = useState<HeldFolder | null>(null)
  const [errors, setErrors] = useState<Record<number, string>>({})
  const why = (f: HeldFolder) =>
    f.reason === 'FolderMissing'
      ? t('The folder isn’t there. Is its drive or share connected?')
      : f.reason === 'FolderEmpty'
        ? t('The folder is empty but held {count, plural, one {# file} other {# files}}. Is its drive or share mounted?', { count: f.known })
        : t('{missing, number} of its {count, plural, one {# file} other {# files}} went at once. If that’s a clear-out, it goes through by itself after a day of scans finding the same.', { missing: f.missing, count: f.known })

  async function confirm(f: HeldFolder) {
    setAsking(null)
    try {
      await confirmHeldRemoval(f)
      onConfirmed()
    } catch (e) {
      setErrors((x) => ({ ...x, [f.folderId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('Couldn’t confirm it') }))
      if (e instanceof ApiError && e.status === 409) onConfirmed() // Show what the scans found now.
    }
  }

  return (
    <HealthGroup
      title={t('Folders on hold')}
      count={folders.length}
      lede={t('Scans are keeping these folders’ titles, in case a drive or share is having trouble. Titles you deleted on purpose stay listed until you confirm it.')}
    >
      {folders.map((f) => (
        <div key={f.folderId} className="health-row health-row-held">
          <span className="health-row-main">
            <span className="health-row-text health-row-wraps">
              <strong>{f.library}</strong>
              <code className="health-row-path">{f.path}</code>
              <span className="muted">{why(f)}</span>
              {errors[f.folderId] && (
                <span className="error" role="alert">
                  {errors[f.folderId]}
                </span>
              )}
            </span>
          </span>
          {f.canConfirm &&
            (f.confirmed ? (
              <span className="tag">{t('Removal confirmed, scanning')}</span>
            ) : (
              <button className="button danger small" onClick={() => setAsking(f)}>
                {t('Confirm removal')}
              </button>
            ))}
        </div>
      ))}
      {asking && (
        <ConfirmDialog
          title={t('Remove {what} from {library}?', { what: heldNoun(asking), library: asking.library })}
          cancelLabel={t('Not yet')}
          confirmLabel={t('Remove from Flexarr')}
          danger
          onClose={() => setAsking(null)}
          onConfirm={() => confirm(asking)}
        >
          <p>{t('Flexarr can’t find them in this folder:')}</p>
          <p>
            <code className="confirm-dialog-path">{asking.path}</code>
          </p>
          <p>
            {t('Flexarr hides them now and removes them a day later. Watch history is kept for 30 days in case they come back. No files are touched. If the drive is just disconnected, reconnect it instead.')}
          </p>
        </ConfirmDialog>
      )}
    </HealthGroup>
  )
}

/** What a held folder's missing files are, by the kind of library: "4 films". */
function heldNoun(f: HeldFolder) {
  return t(
    '{type, select, Movies {{count, plural, one {# movie} other {# movies}}} TvShows {{count, plural, one {# episode} other {# episodes}}} HomeVideos {{count, plural, one {# video} other {# videos}}} Music {{count, plural, one {# track} other {# tracks}}} Audiobooks {{count, plural, one {# audiobook file} other {# audiobook files}}} other {{count, plural, one {# file} other {# files}}}}',
    { type: f.libraryType ?? 'other', count: f.missing },
  )
}

/** In place of a search button: Sonarr or Radarr already have a new copy on the way. */
function DownloadingNote({ state }: { state: LibraryHealth['downloading'][string] }) {
  const status = state.status?.toLowerCase() ?? ''
  const text =
    state.progress >= 0.999 || status.includes('import')
      ? t('Downloaded, waiting to import')
      : status.includes('queued') || status.includes('delay')
        ? t('Queued to download')
        : t('Downloading, {percent}%', { percent: Math.round(state.progress * 100) })
  return <span className="tag health-downloading">{text}</span>
}

/** Something to merge: albums of one release, or one artist's spellings. */
type Merging = { kind: 'albums' | 'artists'; items: ItemSummary[] }

/**
 * Music's groups: albums without a cover (Fix match on each), one release twice and one artist under two names (each
 * with Merge, which asks which to keep), and albums with tracks twice (to tidy in Lidarr or the Finder).
 */
function MusicHealth({ data, onChanged }: { data: LibraryHealth; onChanged: () => void }) {
  const [matching, setMatching] = useState<string>()
  const [merging, setMerging] = useState<Merging>()
  const [keep, setKeep] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const coverless = data.albumsWithoutCover ?? []
  const twice = data.duplicateAlbums ?? []
  const repeated = data.albumsWithRepeatedTracks ?? []
  const split = data.splitArtists ?? []

  function startMerge(m: Merging) {
    setMerging(m)
    setKeep(m.items[0].id)
    setError(undefined)
  }

  async function merge() {
    if (!merging || !keep) return
    setBusy(true)
    setError(undefined)
    try {
      const others = merging.items.filter((i) => i.id !== keep).map((i) => i.id)
      await (merging.kind === 'albums' ? mergeAlbums(keep, others) : mergeArtists(keep, others))
      setMerging(undefined)
      onChanged()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  const names = (items: ItemSummary[]) => items.map((i) => i.title).join(', ')

  return (
    <>
      <HealthGroup title={t('Albums without a cover')} count={coverless.length} lede={t('No cover beside the files, in them or from Lidarr. Fix the match to choose one.')}>
        {coverless.map((item) => (
          <div key={item.id} className="health-row">
            <HealthRowMain item={item} detail={item.seriesTitle ?? t('No cover')} />
            <button className="button quiet small" onClick={() => setMatching(item.id)}>
              {t('Fix match')}
            </button>
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title={t('Albums twice')} count={twice.length} lede={t('The same release more than once. Merge makes them one album; the files stay where they are.')}>
        {twice.map((d) => (
          <div key={d.albums.map((a) => a.id).join()} className="health-row">
            <HealthRowMain item={d.albums[0]} detail={[d.artist, t('{count, plural, one {# copy} other {# copies}}: {names}', { count: d.albums.length, names: names(d.albums) })].filter(Boolean).join('\u2003')} />
            <button className="button quiet small" onClick={() => startMerge({ kind: 'albums', items: d.albums })}>
              {t('Merge')}
            </button>
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title={t('Artists under two names')} count={split.length} lede={t('One artist filed under different spellings, usually from their files’ tags. Merge makes them one; the files stay where they are.')}>
        {split.map((s) => (
          <div key={s.artists.map((a) => a.id).join()} className="health-row">
            <HealthRowMain item={s.artists[0]} detail={names(s.artists)} />
            <button className="button quiet small" onClick={() => startMerge({ kind: 'artists', items: s.artists })}>
              {t('Merge')}
            </button>
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title={t('Tracks twice')} count={repeated.length} lede={t('Albums with the same track more than once, usually two copies in one folder. Remove the spare in Lidarr or the Finder.')}>
        {repeated.map((r) => (
          <div key={r.album.id} className="health-row">
            <HealthRowMain item={r.album} detail={[r.artist, t('{count, plural, one {# track twice} other {# tracks twice}}', { count: r.repeated })].filter(Boolean).join('\u2003')} />
          </div>
        ))}
      </HealthGroup>

      {matching && <MusicMatchById id={matching} onFixed={onChanged} onClose={() => setMatching(undefined)} />}
      {merging && (
        <ConfirmDialog
          title={merging.kind === 'albums' ? t('Merge these albums?') : t('Merge these artists?')}
          confirmLabel={t('Merge')}
          busy={busy}
          error={error}
          onClose={() => setMerging(undefined)}
          onConfirm={() => void merge()}
        >
          <p>
            {merging.kind === 'albums'
              ? t('Which one to keep? The others’ tracks move to it, and new ones land there too. No files are moved or deleted.')
              : t('Which one to keep? The others’ albums move to it, and new ones land there too. No files are moved or deleted.')}
          </p>
          <div className="health-merge-choices" role="radiogroup" aria-label={t('Keep')}>
            {merging.items.map((i) => (
              <label key={i.id} className="health-merge-choice">
                <input type="radio" name="keep" checked={keep === i.id} onChange={() => setKeep(i.id)} disabled={busy} />
                <span>{i.title}</span>
              </label>
            ))}
          </div>
        </ConfirmDialog>
      )}
    </>
  )
}

/** A titled group of problems with a count, a line on what it means, and an optional action for all of them. Nothing, when there are none. */
function HealthGroup({ title, count, lede, action, shown, children }: { title: string; count: number; lede: string; action?: ReactNode; shown?: boolean; children: ReactNode }) {
  if (count === 0 && !shown) return null
  return (
    <section className="health-group">
      <header>
        <div>
          <h3 className="settings-subhead">
            {title} <span className="health-count">{count.toLocaleString(locale())}</span>
          </h3>
          <p className="muted">{lede}</p>
        </div>
        {action}
      </header>
      <div className="panel health-rows">{children}</div>
    </section>
  )
}

/** The poster, the title (an episode's show, number and name), and what's wrong. */
function HealthRowMain({ item, file, detail }: { item: ItemSummary | null; file?: string; detail: string }) {
  return (
    <span className="health-row-main">
      <span className="health-row-art">
        {item && <Artwork src={item.artwork.poster} title={item.title} width={92} shape={item.kind === 'Album' || item.kind === 'Artist' ? 'square' : 'poster'} />}
      </span>
      <span className="health-row-text">
        {item ? (
          <Link to={item.kind === 'Episode' && item.seriesId ? `/item/${item.id}` : `/item/${item.id}`} className="health-row-title">
            {item.kind === 'Episode' ? `${item.seriesTitle ?? ''} ${episodeCode(item)} ${item.title}` : `${item.title}${item.year ? ` (${item.year})` : ''}`}
          </Link>
        ) : (
          <strong>{t('Unknown title')}</strong>
        )}
        <span className="muted">
          {detail}
          {file && (
            <>
              {'\u2003'}
              <span title={file}>{fileName(file)}</span>
            </>
          )}
        </span>
      </span>
    </span>
  )
}
