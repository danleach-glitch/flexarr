import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError, getLibraryHealth, confirmHeldRemoval, keepMatch, removeDuplicates, deleteCopy, getHealthCheck, startHealthCheck, type ItemSummary, type LibraryHealth, type HeldFolder, searchAgain } from '../../api/client'
import { Artwork } from '../../components/Artwork'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { FixMatchDialog } from '../../components/FixMatchDialog'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { episodeCode, formatSize, relativeTime } from '../../lib/format'
import { SettingsHeader } from './shared'

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
  const labels = { Missing: 'Missing from disk', Empty: 'Empty files', Unreadable: 'Still unreadable' } as const
  return (
    <div className="panel health-check">
      <div className="health-check-head">
        <div>
          <strong>Health check</strong>
          <p className="muted">
            {running && report
              ? `Checking files: ${report.checked.toLocaleString()} of ${report.total.toLocaleString()}`
              : report?.finishedAt
                ? `Last checked ${relativeTime(report.finishedAt)}: ${report.total.toLocaleString()} files, ${report.problems.length ? `${report.problems.length.toLocaleString()} with problems` : 'no problems'}.`
                : 'Looks at every file: whether it’s still on disk, not empty, and readable.'}
          </p>
        </div>
        <button
          className="button quiet small"
          disabled={running}
          onClick={async () => {
            await startHealthCheck()
            check.reload()
          }}
        >
          {running ? 'Checking…' : 'Run health check'}
        </button>
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
                {labels[kind]} ({found.length.toLocaleString()})
              </h4>
              <ul className="health-check-list">
                {found.slice(0, 100).map(({ file, item }) => (
                  <li key={file.fileId}>
                    <span>
                      <strong>{item ? (item.seriesTitle ? `${item.seriesTitle} ${episodeCode(item)}` : item.title) : 'Unknown title'}</strong>
                      <code>{file.path}</code>
                    </span>
                    {item && (
                      <button
                        className="button quiet small"
                        disabled={!!requested[item.id]}
                        onClick={async () => {
                          try {
                            await searchAgain(item.id)
                            setRequested((r) => ({ ...r, [item.id]: 'Search started' }))
                          } catch (e) {
                            setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Search failed' }))
                          }
                        }}
                      >
                        {requested[item.id] ?? 'Search again'}
                      </button>
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
  if (!data) return <p className="muted">Checking your library…</p>

  async function upgrade(id: string) {
    try {
      await searchAgain(id)
      setRequested((r) => ({ ...r, [id]: 'Search started' }))
    } catch (e) {
      setRequested((r) => ({ ...r, [id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Search failed' }))
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
      setCopyErrors((c) => ({ ...c, [fileId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t delete it' }))
    }
  }

  const score = data.score
  const breakdown = [
    [score.duplicates, 'with spare copies'],
    [score.unreadable, 'unreadable'],
    [score.fileProblems, 'missing or empty files'],
    [score.standardDefinition, 'only in SD'],
    [score.missingArtwork, 'without artwork'],
  ] as const

  // Groups with nothing in them drop out, so what's left is what needs a look; this line says what's fine.
  const clear = [
    data.duplicates.length === 0 && 'every title has a single copy',
    data.unreadable.length === 0 && 'every file opened cleanly',
    data.standardDefinitionTotal === 0 && 'every film has an HD copy',
  ].filter((s): s is string => !!s)

  return (
    <section className="health">
      <SettingsHeader title="Library health" back={{ to: '/settings/libraries', label: 'Libraries' }} />

      <div className="panel health-score" data-grade={score.score >= 95 ? 'good' : score.score >= 80 ? 'fair' : 'poor'}>
        <div className="health-score-number">
          <span className="health-score-value">{score.score}</span>
          <span className="health-score-label">Health score</span>
        </div>
        <div>
          <p className="health-score-summary">
            {score.withProblems === 0
              ? `All ${score.titles.toLocaleString()} films and episodes are in good shape.`
              : `${(score.titles - score.withProblems).toLocaleString()} of ${score.titles.toLocaleString()} films and episodes have no problems.`}
          </p>
          <ul className="health-score-breakdown">
            {breakdown
              .filter(([n]) => n > 0)
              .map(([n, label]) => (
                <li key={label}>
                  <strong>{n.toLocaleString()}</strong> {label}
                </li>
              ))}
            {data.held.length > 0 && (
              <li>
                <strong>{data.held.length.toLocaleString()}</strong> {data.held.length === 1 ? 'folder' : 'folders'} on hold
              </li>
            )}
            {(score.artworkNeedsConnection ?? 0) > 0 && (
              <li>
                <strong>{score.artworkNeedsConnection!.toLocaleString()}</strong> without artwork:{' '}
                <Link to="/settings/connections">connect Radarr or Sonarr</Link> to fill it in
              </li>
            )}
          </ul>
        </div>
      </div>

      <HealthCheckPanel onFinished={reload} />

      {data.held.length > 0 && <HeldFolders folders={data.held} onConfirmed={reload} />}

      {clear.length > 0 && <p className="muted health-clear">All clear: {clear.join(', ')}.</p>}

      <HealthGroup
        title="Spare copies"
        count={data.duplicates.length}
        // Stays while spares are removed, so the last of them going doesn't take the progress with it.
        shown={removing !== null}
        lede={`More than one file for the same film or episode. Removing spares frees ${formatSize(data.reclaimableBytes)}; the best copy, and the one Sonarr or Radarr use, always stay.`}
        action={
          data.duplicates.length > 0 && (
            <button className="button danger small" disabled={removing !== null && removing.done < removing.total} onClick={() => setConfirmAll(true)}>
              Remove all spare copies ({formatSize(data.reclaimableBytes)})
            </button>
          )
        }
      >
        {removing && (
          <div className="health-progress" role="status">
            <span>
              {removing.done < removing.total
                ? `Removing spare copies: ${removing.done.toLocaleString()} of ${removing.total.toLocaleString()} titles`
                : removing.total - removing.kept - removing.failed > 0
                  ? `Removed spare copies of ${(removing.total - removing.kept - removing.failed).toLocaleString()} titles, freeing ${formatSize(removing.freed)}.`
                  : 'Nothing was removed.'}
              {removing.done === removing.total &&
                removing.kept > 0 &&
                ` ${removing.kept.toLocaleString()} kept every copy: Sonarr or Radarr use a different copy from the best one. Open a title to delete the copy you don’t want.`}
              {removing.failed > 0 && ` ${removing.failed} couldn’t be removed (see the server log).`}
            </span>
            <span className="scan-track" aria-hidden="true">
              <span style={{ width: `${(removing.done / Math.max(1, removing.total)) * 100}%` }} />
            </span>
          </div>
        )}
        {data.duplicates.slice(0, shown).map((d) => (
          <details key={d.item.id} className="health-row-expandable">
            <summary>
              <HealthRowMain item={d.item} detail={`${d.files.length} copies, ${formatSize(d.reclaimableBytes)} spare`} />
              <button
                className="button quiet small"
                onClick={(e) => {
                  e.preventDefault()
                  void removeSpares([d.item.id])
                }}
              >
                Remove spares
              </button>
            </summary>
            <ul className="health-copies">
              {d.files.map((f) => {
                // Copies identical to the best: which one stays is whichever Sonarr or Radarr use.
                const best = d.files.find((x) => x.keep)!
                const same = (x: typeof f) => x.size === best.size && x.resolution === best.resolution
                const identical = same(f) && d.files.filter(same).length > 1
                return (
                  <li key={f.id}>
                    <span className={f.keep || identical ? 'tag keep' : 'tag'}>{identical ? 'Identical' : f.keep ? 'Best' : 'Spare'}</span>
                    <span title={f.path}>{fileName(f.path)}</span>
                    <span className="muted">{[f.resolution, f.videoCodec?.toUpperCase(), formatSize(f.size)].filter(Boolean).join(', ')}</span>
                    <button className="text-button" onClick={() => removeCopy(f.id)}>
                      Delete this copy
                    </button>
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
            Show more ({(data.duplicates.length - shown).toLocaleString()} left)
          </button>
        )}
      </HealthGroup>

      <HealthGroup title="Files that can’t be read" count={data.unreadable.length} lede="Usually a damaged or unfinished download. A new copy fixes playback.">
        {data.unreadable.map((u) => (
          <div key={u.fileId} className="health-row">
            <HealthRowMain item={u.item} file={u.path} detail={`Can’t be read: ${u.error.split(': ').at(-1)?.toLowerCase()}`} />
            {u.item &&
              (data.downloading[u.item.id] ? (
                <DownloadingNote state={data.downloading[u.item.id]} />
              ) : (
                <button className="button quiet small" onClick={() => upgrade(u.item!.id)} disabled={!!requested[u.item.id]}>
                  {requested[u.item.id] ?? 'Get a new copy'}
                </button>
              ))}
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title="Only in standard definition" count={data.standardDefinitionTotal} lede="Your best copy is below 720p. Radarr can look for a better one.">
        {data.standardDefinition.map((item) => (
          <div key={item.id} className="health-row">
            <HealthRowMain item={item} detail="Best copy is SD" />
            {data.downloading[item.id] ? (
              <DownloadingNote state={data.downloading[item.id]} />
            ) : (
              <button className="button quiet small" onClick={() => upgrade(item.id)} disabled={!!requested[item.id]}>
                {requested[item.id] ?? 'Find a better copy'}
              </button>
            )}
          </div>
        ))}
      </HealthGroup>

      {data.matchedByTitle.length > 0 && (
        <HealthGroup
          title="Check these matches"
          count={data.matchedByTitle.length}
          lede="Sonarr and Radarr don’t manage these, so their details came from searching for the folder’s name, which can find the wrong one: an older show with the same name, or a remake. Fixing a match adds the title to Sonarr or Radarr. Ignore one that’s right and it stays as it is."
        >
          {data.matchedByTitle.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail={requested[item.id] ?? 'Matched by title'} />
              <div className="health-row-actions">
                <button
                  className="button quiet small"
                  disabled={requested[item.id] === 'Keeping this match…'}
                  onClick={async () => {
                    setRequested((r) => ({ ...r, [item.id]: 'Keeping this match…' }))
                    try {
                      await keepMatch(item.id)
                      reload()
                    } catch (e) {
                      setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t keep this match' }))
                    }
                  }}
                >
                  Ignore
                </button>
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  Fix match
                </button>
              </div>
            </div>
          ))}
        </HealthGroup>
      )}

      {data.missingArtwork.length > 0 && (
        <HealthGroup
          title="Missing artwork"
          count={data.missingArtwork.length}
          lede={
            score.missingArtwork === 0 && (score.artworkNeedsConnection ?? 0) > 0
              ? 'Artwork comes from Radarr and Sonarr. Connect them in Settings › Connections to fill it in.'
              : 'Not found in Sonarr or Radarr. Fix the match to choose the right title, or rename the folder to “Title (Year)”.'
          }
        >
          {data.missingArtwork.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail="No poster or details" />
              {(item.kind === 'Movie' || item.kind === 'Series') && (
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  Fix match
                </button>
              )}
            </div>
          ))}
        </HealthGroup>
      )}

      {matching && <FixMatchDialog item={matching} onFixed={reload} onClose={() => setMatching(undefined)} />}

      {confirmAll && (
        <ConfirmDialog
          title={`Remove ${data.duplicates.length.toLocaleString()} spare copies?`}
          cancelLabel="Keep them"
          confirmLabel="Remove spare copies"
          danger
          onClose={() => setConfirmAll(false)}
          onConfirm={() => {
            setConfirmAll(false)
            void removeSpares(data.duplicates.map((d) => d.item.id))
          }}
        >
          <p>
            This deletes the spare files from disk, freeing {formatSize(data.reclaimableBytes)}. For each title the best copy, and the one Sonarr or Radarr use, are kept.
            You can’t undo this.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

const fileName = (path: string) => path.split('/').at(-1) ?? path

/**
 * Folders whose files the scans are keeping: they looked unmounted or cleared out. If they really were cleared out,
 * confirming lets the next scan hide them (and remove them a day later).
 */
function HeldFolders({ folders, onConfirmed }: { folders: HeldFolder[]; onConfirmed: () => void }) {
  const [asking, setAsking] = useState<HeldFolder | null>(null)
  const [errors, setErrors] = useState<Record<number, string>>({})
  const why = (f: HeldFolder) =>
    f.reason === 'FolderMissing'
      ? 'The folder isn’t there. Is its drive or share connected?'
      : f.reason === 'FolderEmpty'
        ? `The folder is empty but held ${f.known.toLocaleString()} files. Is its drive or share mounted?`
        : `${f.missing.toLocaleString()} of its ${f.known.toLocaleString()} files went at once. If that’s a clear-out, it goes through by itself after a day of scans finding the same.`

  async function confirm(f: HeldFolder) {
    setAsking(null)
    try {
      await confirmHeldRemoval(f)
      onConfirmed()
    } catch (e) {
      setErrors((x) => ({ ...x, [f.folderId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t confirm it' }))
      if (e instanceof ApiError && e.status === 409) onConfirmed() // Show what the scans found now.
    }
  }

  return (
    <HealthGroup
      title="Folders on hold"
      count={folders.length}
      lede="Scans are keeping these folders’ titles, in case a drive or share is having trouble. Titles you deleted on purpose stay listed until you confirm it."
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
              <span className="tag">Removal confirmed, scanning</span>
            ) : (
              <button className="button danger small" onClick={() => setAsking(f)}>
                Confirm removal
              </button>
            ))}
        </div>
      ))}
      {asking && (
        <ConfirmDialog
          title={`Remove ${asking.missing.toLocaleString()} ${heldNoun(asking)} from ${asking.library}?`}
          cancelLabel="Not yet"
          confirmLabel="Remove from Flexarr"
          danger
          onClose={() => setAsking(null)}
          onConfirm={() => void confirm(asking)}
        >
          <p>Flexarr can’t find them in this folder:</p>
          <p>
            <code className="confirm-dialog-path">{asking.path}</code>
          </p>
          <p>
            Flexarr hides them now and removes them a day later. Watch history is kept for 30 days in case they come back. No files are touched. If the
            drive is just disconnected, reconnect it instead.
          </p>
        </ConfirmDialog>
      )}
    </HealthGroup>
  )
}

/** What a held folder's missing files are, by the kind of library: "4 films". */
function heldNoun(f: HeldFolder) {
  const one = f.missing === 1
  return f.libraryType === 'Movies' ? (one ? 'film' : 'films') : f.libraryType === 'TvShows' ? (one ? 'episode' : 'episodes') : f.libraryType === 'Music' ? (one ? 'track' : 'tracks') : one ? 'file' : 'files'
}

/** In place of a search button: Sonarr or Radarr already have a new copy on the way. */
function DownloadingNote({ state }: { state: LibraryHealth['downloading'][string] }) {
  const status = state.status?.toLowerCase() ?? ''
  const text =
    state.progress >= 0.999 || status.includes('import')
      ? 'Downloaded, waiting to import'
      : status.includes('queued') || status.includes('delay')
        ? 'Queued to download'
        : `Downloading ${Math.round(state.progress * 100)}%`
  return <span className="tag health-downloading">{text}</span>
}

/** A titled group of problems with a count, a line on what it means, and an optional action for all of them. Nothing, when there are none. */
function HealthGroup({ title, count, lede, action, shown, children }: { title: string; count: number; lede: string; action?: ReactNode; shown?: boolean; children: ReactNode }) {
  if (count === 0 && !shown) return null
  return (
    <section className="health-group">
      <header>
        <div>
          <h3 className="settings-subhead">
            {title} <span className="health-count">{count.toLocaleString()}</span>
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
      <span className="health-row-art">{item && <Artwork src={item.artwork.poster} title={item.title} width={92} shape="poster" />}</span>
      <span className="health-row-text">
        {item ? (
          <Link to={item.kind === 'Episode' && item.seriesId ? `/item/${item.id}` : `/item/${item.id}`} className="health-row-title">
            {item.kind === 'Episode' ? `${item.seriesTitle ?? ''} ${episodeCode(item)} ${item.title}` : `${item.title}${item.year ? ` (${item.year})` : ''}`}
          </Link>
        ) : (
          <strong>Unknown title</strong>
        )}
        <span className="muted">
          {detail}
          {file && (
            <>
              {' · '}
              <span title={file}>{fileName(file)}</span>
            </>
          )}
        </span>
      </span>
    </span>
  )
}
