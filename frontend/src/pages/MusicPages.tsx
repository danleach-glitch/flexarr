import { useState, type CSSProperties, type MouseEvent } from 'react'
import { Link } from 'react-router'
import { getRadio, getTracks, imageUrl, type ItemDetail, type ItemSummary } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/Cards'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { More, Play, Radio } from '../components/icons'
import { LyricsPanel } from '../components/LyricsPanel'
import { useAudioPlayer, useAudioTime, useMusicPage, type AudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock, formatDuration } from '../lib/format'

/** A fair shuffle (Fisher–Yates); sorting by a random comparator isn't one. */
export function shuffled(tracks: ItemSummary[]): ItemSummary[] {
  const out = [...tracks]
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[out[i], out[j]] = [out[j], out[i]]
  }
  return out
}

/** The queue actions for a set of tracks, as a menu offers them. */
function queueActions(player: AudioPlayer, tracks: () => Promise<ItemSummary[]> | ItemSummary[]): MenuAction[] {
  return [
    { label: 'Play next', run: async () => player.playNext(await tracks()) },
    { label: 'Add to queue', run: async () => player.enqueue(await tracks()) },
  ]
}

/** The ⋯ beside Play and Shuffle, with the queue actions for the whole album or artist. */
function MoreButton({ label, actions, disabled }: { label: string; actions: MenuAction[]; disabled?: boolean }) {
  const [at, setAt] = useState<MenuPoint | null>(null)
  return (
    <>
      <button
        className="button quiet more-button"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={at !== null}
        disabled={disabled}
        onClick={(e) => {
          const r = e.currentTarget.getBoundingClientRect()
          setAt(at ? null : { x: r.left, y: r.bottom + 6 })
        }}
      >
        <More />
      </button>
      {at && <Menu label={label} at={at} actions={actions} onClose={() => setAt(null)} />}
    </>
  )
}

/** An album: cover, facts and its tracklist. Discs are separated when there's more than one. */
export function AlbumPage({ detail }: { detail: ItemDetail }) {
  const player = useAudioPlayer()
  useMusicPage()
  const { item, children: tracks } = detail
  const artist = detail.ancestors[0]
  const total = tracks.reduce((sum, t) => sum + (t.durationSeconds ?? 0), 0)
  const discs = new Set(tracks.map((t) => t.discNumber ?? 1)).size
  // One track's menu at a time: which, and where it opened.
  const [trackMenu, setTrackMenu] = useState<{ index: number; at: MenuPoint } | null>(null)

  const openMenu = (index: number, at: MenuPoint) => (e: MouseEvent) => {
    e.preventDefault()
    setTrackMenu(trackMenu?.index === index ? null : { index, at })
  }

  return (
    <article className="music-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{item.title}</h1>
          <p className="facts">
            {artist && (
              <Link to={`/item/${artist.id}`} className="music-artist-link">
                {artist.title}
              </Link>
            )}
            {item.year && <span>{item.year}</span>}
            <span>{tracks.length === 1 ? '1 track' : `${tracks.length} tracks`}</span>
            {total > 0 && <span>{formatDuration(total)}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => player.play(tracks, 0)} disabled={tracks.length === 0}>
              <Play /> Play
            </button>
            <button className="button quiet" onClick={() => player.play(shuffled(tracks), 0)} disabled={tracks.length < 2}>
              Shuffle
            </button>
            <MoreButton label={`More for ${item.title}`} actions={queueActions(player, () => tracks)} disabled={tracks.length === 0} />
          </div>
        </div>
      </header>

      <ol className="tracklist">
        {tracks.map((track, i) => {
          const isCurrent = player.current?.id === track.id
          const newDisc = discs > 1 && (i === 0 || (tracks[i - 1].discNumber ?? 1) !== (track.discNumber ?? 1))
          return (
            <li key={track.id}>
              {newDisc && <h2 className="tracklist-disc">Disc {track.discNumber ?? 1}</h2>}
              <div className="track-row" onContextMenu={(e) => openMenu(i, { x: e.clientX, y: e.clientY })(e)}>
                <button className={isCurrent ? 'track current' : 'track'} onClick={() => player.play(tracks, i)} aria-current={isCurrent || undefined}>
                  <span className="track-number">{isCurrent && player.playing ? <Play /> : (track.trackNumber ?? i + 1)}</span>
                  <span className="track-title">{track.title}</span>
                  <span className="track-time">{track.durationSeconds ? formatClock(track.durationSeconds) : ''}</span>
                </button>
                <button
                  className="icon-button track-menu-button"
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
              </div>
            </li>
          )
        })}
      </ol>
      <PlayingLyrics tracks={tracks} />
      {trackMenu && tracks[trackMenu.index] && (
        <Menu
          label={`More for ${tracks[trackMenu.index].title}`}
          at={trackMenu.at}
          actions={[{ label: 'Play', run: () => player.play(tracks, trackMenu.index) }, ...queueActions(player, () => [tracks[trackMenu.index]])]}
          onClose={() => setTrackMenu(null)}
        />
      )}
    </article>
  )
}

/**
 * Lyrics for the track playing from this album, under its tracklist. A component of its own because the time ticks
 * every second, and only this part of the page needs to follow it.
 */
function PlayingLyrics({ tracks }: { tracks: ItemSummary[] }) {
  const player = useAudioPlayer()
  const time = useAudioTime()
  const current = player.current && tracks.some((t) => t.id === player.current?.id) ? player.current : undefined
  if (!current) return null
  return <LyricsPanel trackId={current.id} currentTime={time} title={current.title} onSeek={player.seek} />
}

/** An artist and their albums, newest first. Play and Shuffle take every track they have; Radio mixes in artists like them. */
export function ArtistPage({ detail }: { detail: ItemDetail }) {
  const player = useAudioPlayer()
  useMusicPage()
  const { item, children: albums } = detail
  const [loading, setLoading] = useState(false)

  // The tracks are fetched when asked for, not with the page: an artist with a hundred albums shouldn't make every
  // visit load them all.
  const playAll = async (shuffle: boolean) => {
    setLoading(true)
    try {
      const tracks = await getTracks(item.id)
      if (tracks.length > 0) player.play(shuffle ? shuffled(tracks) : tracks, 0)
    } finally {
      setLoading(false)
    }
  }

  // A station the server picks: this artist and others sharing their genres, leaning towards what you play.
  const startRadio = async () => {
    setLoading(true)
    try {
      const tracks = await getRadio(item.id)
      if (tracks.length > 0) player.play(tracks, 0)
    } finally {
      setLoading(false)
    }
  }

  return (
    <article className="music-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{item.title}</h1>
          <p className="facts">
            <span>{albums.length === 1 ? '1 album' : `${albums.length} albums`}</span>
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => void playAll(false)} disabled={albums.length === 0 || loading}>
              <Play /> Play
            </button>
            <button className="button quiet" onClick={() => void playAll(true)} disabled={albums.length === 0 || loading}>
              Shuffle
            </button>
            <button className="button quiet" onClick={() => void startRadio()} disabled={albums.length === 0 || loading}>
              <Radio /> Radio
            </button>
            <MoreButton label={`More for ${item.title}`} actions={queueActions(player, () => getTracks(item.id))} disabled={albums.length === 0} />
          </div>
        </div>
      </header>
      <div className="poster-grid music-albums">
        {albums.map((album) => (
          <PosterCard key={album.id} item={album} />
        ))}
      </div>
    </article>
  )
}
