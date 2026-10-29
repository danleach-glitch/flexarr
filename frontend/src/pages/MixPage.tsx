import { useState, type CSSProperties, type MouseEvent } from 'react'
import { useParams } from 'react-router'
import { getMix, imageUrl } from '../api/client'
import { More, Play } from '../components/icons'
import { Menu, type MenuPoint } from '../components/ItemMenu'
import { PlaylistCover } from '../components/PlaylistCover'
import { useAudioPlayer, useMusicPage } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { formatClock, formatDuration } from '../lib/format'
import { MoreButton, queueActions, shuffled } from './MusicPages'

/**
 * One "Made for you" mix: its cover mosaic, name and what it's made of, Play and Shuffle, and today's tracks in order.
 * It looks like a playlist's page, but the server made it and remakes it tomorrow, so there's nothing to edit.
 */
export function MixPage() {
  const { id = '' } = useParams()
  const { data, error } = useApi((signal) => getMix(id, signal), [id])
  const player = useAudioPlayer()
  useMusicPage()
  // One track's menu at a time: which, and where it opened.
  const [trackMenu, setTrackMenu] = useState<{ index: number; at: MenuPoint } | null>(null)

  if (error) return <p className="page-message error">{error.message.includes('404') ? 'That mix was yesterday’s. Today’s are on Home.' : error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  const { summary, tracks } = data
  const total = tracks.reduce((sum, t) => sum + (t.durationSeconds ?? 0), 0)
  const glow = summary.covers[0]
  const accent = tracks.find((t) => t.accentColor)?.accentColor

  const openMenu = (index: number, at: MenuPoint) => (e: MouseEvent) => {
    e.preventDefault()
    setTrackMenu(trackMenu?.index === index ? null : { index, at })
  }

  return (
    <article className="music-page playlist-page mix-page" style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {glow && <img src={imageUrl(glow, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <PlaylistCover playlist={{ cover: null, covers: summary.covers, name: summary.name }} width={500} />
        </div>
        <div className="music-header-text">
          <p className="detail-kicker">Made for you</p>
          <h1>{summary.name}</h1>
          <p className="facts">
            <span>{summary.description}</span>
            <span>{tracks.length === 1 ? '1 track' : `${tracks.length} tracks`}</span>
            {total > 0 && <span>{formatDuration(total)}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => player.play(tracks, 0, { openNowPlaying: true })} disabled={tracks.length === 0}>
              <Play /> Play
            </button>
            <button className="button quiet" onClick={() => player.play(shuffled(tracks), 0, { openNowPlaying: true })} disabled={tracks.length < 2}>
              Shuffle
            </button>
            <MoreButton label={`More for ${summary.name}`} actions={queueActions(player, () => tracks)} disabled={tracks.length === 0} />
          </div>
          <p className="muted mix-note">New every day, from what you play.</p>
        </div>
      </header>

      <ol className="tracklist playlist-tracks" aria-label={`Tracks in ${summary.name}`}>
        {tracks.map((track, i) => {
          const isCurrent = player.current?.id === track.id
          return (
            <li key={track.id} className="playlist-entry mix-entry" onContextMenu={(e) => openMenu(i, { x: e.clientX, y: e.clientY })(e)}>
              <button className={isCurrent ? 'track current' : 'track'} onClick={() => player.play(tracks, i)} aria-current={isCurrent || undefined}>
                <span className="track-number">{isCurrent && player.playing ? <Play /> : i + 1}</span>
                <span className="playlist-track-art" aria-hidden="true">
                  {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 92)} alt="" loading="lazy" />}
                </span>
                <span className="playlist-track-text">
                  <span className="track-title">{track.title}</span>
                  <span className="muted">{track.seriesTitle}</span>
                </span>
                <span className="track-time">{track.durationSeconds ? formatClock(track.durationSeconds) : ''}</span>
              </button>
              <span className="queue-actions">
                <button
                  className="icon-button"
                  aria-label={`More for ${track.title}`}
                  aria-haspopup="menu"
                  aria-expanded={trackMenu?.index === i}
                  onClick={(e) => {
                    const r = e.currentTarget.getBoundingClientRect()
                    openMenu(i, { x: r.right - 220, y: r.bottom + 6 })(e)
                  }}
                >
                  <More />
                </button>
              </span>
            </li>
          )
        })}
      </ol>

      {trackMenu && tracks[trackMenu.index] && (
        <Menu
          label={`More for ${tracks[trackMenu.index].title}`}
          at={trackMenu.at}
          actions={[{ label: 'Play', run: () => player.play(tracks, trackMenu.index) }, ...queueActions(player, () => [tracks[trackMenu.index]], tracks[trackMenu.index])]}
          onClose={() => setTrackMenu(null)}
        />
      )}
    </article>
  )
}
