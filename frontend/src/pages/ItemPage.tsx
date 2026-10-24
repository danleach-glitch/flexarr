import { Link, useParams } from 'react-router'
import { getItem, type ItemDetail, type ItemSummary, type MediaFile } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/PosterCard'
import { useApi } from '../hooks/useApi'
import { episodeCode, formatBitrate, formatChannels, formatDuration, formatSize, rangeLabel } from '../lib/format'

export function ItemPage() {
  const { id = '' } = useParams()
  const { data, error } = useApi((signal) => getItem(id, signal), [id])

  if (error) return <p className="error">{error.message}</p>
  if (!data) return <p className="muted">Loading…</p>

  const { item, ancestors, children, files } = data
  const best = files[0]

  return (
    <article className="item-page">
      {ancestors.length > 0 && (
        <nav className="breadcrumbs" aria-label="Breadcrumb">
          {ancestors.map((a) => (
            <Link key={a.id} to={`/item/${a.id}`}>
              {a.title}
            </Link>
          ))}
        </nav>
      )}

      <header className="item-hero">
        <Artwork title={ancestors[0]?.title ?? item.title} />
        <div className="item-hero-text">
          <h1>{item.title}</h1>
          <p className="item-meta">
            {[
              item.kind === 'Episode' ? episodeCode(item) : null,
              item.year,
              formatDuration(item.durationSeconds),
              item.resolution,
              best ? rangeLabel[best.videoRange] : null,
              item.kind === 'Series' ? `${children.length} ${children.length === 1 ? 'season' : 'seasons'}` : null,
              item.kind === 'Season' ? `${children.length} ${children.length === 1 ? 'episode' : 'episodes'}` : null,
            ]
              .filter(Boolean)
              .map((part) => (
                <span key={String(part)}>{part}</span>
              ))}
          </p>
          <ExternalLinks detail={data} />
        </div>
      </header>

      {item.kind === 'Series' && <ChildGrid title="Seasons" items={children} />}
      {item.kind === 'Season' && <EpisodeList episodes={children} />}

      {files.length > 0 && (
        <section>
          <h2 className="section-title">{files.length > 1 ? `${files.length} versions` : 'File'}</h2>
          {files.map((file) => (
            <FileDetails key={file.id} file={file} />
          ))}
        </section>
      )}
    </article>
  )
}

function ExternalLinks({ detail }: { detail: ItemDetail }) {
  const links = [
    detail.imdbId && { label: 'IMDb', href: `https://www.imdb.com/title/${detail.imdbId}/` },
    detail.tmdbId && { label: 'TMDB', href: `https://www.themoviedb.org/${detail.item.kind === 'Movie' ? 'movie' : 'tv'}/${detail.tmdbId}` },
    detail.tvdbId && { label: 'TVDB', href: `https://thetvdb.com/?tab=series&id=${detail.tvdbId}` },
  ].filter((l): l is { label: string; href: string } => !!l)

  if (links.length === 0) return null
  return (
    <p className="external-links">
      {links.map((l) => (
        <a key={l.label} href={l.href} target="_blank" rel="noreferrer">
          {l.label}
        </a>
      ))}
    </p>
  )
}

function ChildGrid({ title, items }: { title: string; items: ItemSummary[] }) {
  return (
    <section>
      <h2 className="section-title">{title}</h2>
      <div className="poster-grid">
        {items.map((child) => (
          <PosterCard key={child.id} item={child} />
        ))}
      </div>
    </section>
  )
}

function EpisodeList({ episodes }: { episodes: ItemSummary[] }) {
  return (
    <section>
      <h2 className="section-title">Episodes</h2>
      <ol className="episode-list">
        {episodes.map((ep) => (
          <li key={ep.id}>
            <Link to={`/item/${ep.id}`}>
              <span className="episode-number">{ep.airDate ?? (ep.episodeNumberEnd ? `${ep.episodeNumber}–${ep.episodeNumberEnd}` : ep.episodeNumber)}</span>
              <span className="episode-title">{ep.title}</span>
              <span className="muted">{[formatDuration(ep.durationSeconds), ep.resolution].filter(Boolean).join(' · ')}</span>
            </Link>
          </li>
        ))}
      </ol>
    </section>
  )
}

function FileDetails({ file }: { file: MediaFile }) {
  const video = [file.videoCodec?.toUpperCase(), file.width && file.height ? `${file.width}×${file.height}` : null, rangeLabel[file.videoRange]]
  const audio = [file.audioCodec?.toUpperCase(), formatChannels(file.audioChannels)]

  return (
    <div className="card file-card">
      <code className="file-path">{file.path}</code>
      {file.probeError && <p className="error">Couldn't read this file: {file.probeError}</p>}
      <dl>
        <dt>Container</dt>
        <dd>{file.container?.toUpperCase() ?? '—'}</dd>
        <dt>Video</dt>
        <dd>{video.filter(Boolean).join(' · ') || '—'}</dd>
        <dt>Audio</dt>
        <dd>{audio.filter(Boolean).join(' · ') || '—'}</dd>
        <dt>Size</dt>
        <dd>{[formatSize(file.size), formatBitrate(file.bitrate)].filter(Boolean).join(' · ')}</dd>
        <dt>Duration</dt>
        <dd>{formatDuration(file.durationSeconds) ?? '—'}</dd>
      </dl>
      {file.streams.length > 0 && (
        <details>
          <summary>{file.streams.length} streams</summary>
          <table className="streams">
            <thead>
              <tr>
                <th>#</th>
                <th>Type</th>
                <th>Codec</th>
                <th>Language</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              {file.streams.map((s) => (
                <tr key={s.index}>
                  <td>{s.index}</td>
                  <td>{s.type}</td>
                  <td>
                    {s.codec}
                    {s.profile ? ` (${s.profile})` : ''}
                  </td>
                  <td>{s.language ?? '—'}</td>
                  <td>
                    {[
                      s.title,
                      s.width && s.height ? `${s.width}×${s.height}` : null,
                      formatChannels(s.channels),
                      s.isDefault ? 'default' : null,
                      s.isForced ? 'forced' : null,
                    ]
                      .filter(Boolean)
                      .join(' · ')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
    </div>
  )
}
