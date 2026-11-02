import { useState, type CSSProperties, type MouseEvent, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { getMusicArtistExtras, getMusicRequestStatus, getRadio, getTracks, imageUrl, type AlbumType, type ItemDetail, type ItemSummary } from '../api/client'
import { openAddToPlaylist, type PlaylistAddition } from '../components/AddToPlaylist'
import { Artwork } from '../components/Artwork'
import { AsyncButton } from '../components/AsyncButton'
import { DeleteDialog } from '../components/DeleteDialog'
import { PosterCard } from '../components/Cards'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { More, Play, Radio } from '../components/icons'
import { LyricsPanel } from '../components/LyricsPanel'
import { MusicExternalCard, MusicRequestButton } from '../components/MusicExternalCard'
import { Row } from '../components/Row'
import { useApi } from '../hooks/useApi'
import { useSession } from '../hooks/sessionContext'
import { useAudioPlayer, useAudioTime, useMusicPage, type AudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock, formatDuration } from '../lib/format'

import { shuffled } from '../lib/shuffle'

/**
 * The queue actions for a set of tracks, as a menu offers them, and (given what they belong to: a track, album or
 * artist) a way into a playlist.
 */
export function queueActions(player: AudioPlayer, tracks: () => Promise<ItemSummary[]> | ItemSummary[], item?: PlaylistAddition): MenuAction[] {
  return [
    { label: 'Play next', run: async () => player.playNext(await tracks()) },
    { label: 'Add to queue', run: async () => player.enqueue(await tracks()) },
    ...(item ? [{ label: 'Add to playlist…', run: () => openAddToPlaylist({ id: item.id, title: item.title }) }] : []),
  ]
}

/** What an album's page calls its kind of release: nothing for a plain album, or one nobody has typed. */
export function albumTypeLabel(type: AlbumType | null | undefined): string | null {
  switch (type) {
    case 'ep':
      return 'EP'
    case 'single':
      return 'Single'
    case 'live':
      return 'Live'
    case 'compilation':
      return 'Compilation'
    case 'soundtrack':
      return 'Soundtrack'
    default:
      return null
  }
}

const sectionTitles = ['Albums', 'EPs and singles', 'Live and compilations', 'Other'] as const
type SectionTitle = (typeof sectionTitles)[number]

// Where each kind is shelved. Anything else (plain albums, soundtracks the artist made, albums nobody has typed) is
// an album, so a kind this build doesn't know never hides one.
const shelf: Partial<Record<string, SectionTitle>> = {
  ep: 'EPs and singles',
  single: 'EPs and singles',
  live: 'Live and compilations',
  compilation: 'Live and compilations',
  other: 'Other',
}

/**
 * An artist's albums in the sections a record shop shelves them by, as Spotify lists them: albums, then EPs and
 * singles, then live albums and compilations, then the rest. Within a section they keep the order given (newest
 * first, from the server); a section with nothing in it is left out.
 */
export function albumSections(albums: ItemSummary[]): { title: SectionTitle; albums: ItemSummary[] }[] {
  const grouped = new Map<SectionTitle, ItemSummary[]>()
  for (const album of albums) {
    const title = shelf[album.albumType ?? ''] ?? 'Albums'
    grouped.set(title, [...(grouped.get(title) ?? []), album])
  }
  return sectionTitles.filter((title) => grouped.has(title)).map((title) => ({ title, albums: grouped.get(title)! }))
}

/** The ⋯ beside Play and Shuffle, with the queue actions for the whole album or artist. */
export function MoreButton({ label, actions, disabled }: { label: string; actions: MenuAction[]; disabled?: boolean }) {
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

/** Two groups of menu actions, with a line between them when both have any. */
const andThen = (first: MenuAction[], then: MenuAction[]): MenuAction[] => (first.length && then.length ? [...first, 'divider', ...then] : [...first, ...then])

/**
 * Delete… in an album's or artist's ⋯, for the main profile (locked or not, as for films), and its dialog. Lidarr
 * deletes the files when it manages them; back to the artist (for an album) or the library afterwards.
 */
function useDeletion(detail: ItemDetail): { actions: MenuAction[]; dialog: ReactNode } {
  const { me } = useSession()
  const navigate = useNavigate()
  const [deleting, setDeleting] = useState(false)
  const { item } = detail
  const parent = detail.ancestors[0]
  if (!(me?.isAdmin === true || me?.canUnlock === true)) return { actions: [], dialog: null }
  return {
    actions: [{ label: 'Delete…', run: () => setDeleting(true), danger: true }],
    dialog: deleting ? (
      <DeleteDialog
        item={item}
        onClose={() => setDeleting(false)}
        onDeleted={() => navigate(item.kind === 'Album' && parent ? `/item/${parent.id}` : `/library/${item.libraryId}`, { replace: true })}
      />
    ) : null,
  }
}

/** An album: cover, facts and its tracklist. Discs are separated when there's more than one. */
export function AlbumPage({ detail }: { detail: ItemDetail }) {
  const player = useAudioPlayer()
  useMusicPage()
  const { item, children: tracks } = detail
  const artist = detail.ancestors[0]
  const total = tracks.reduce((sum, t) => sum + (t.durationSeconds ?? 0), 0)
  const discs = new Set(tracks.map((t) => t.discNumber ?? 1)).size
  const kind = albumTypeLabel(item.albumType)
  // One track's menu at a time: which, and where it opened.
  const [trackMenu, setTrackMenu] = useState<{ index: number; at: MenuPoint } | null>(null)
  const deletion = useDeletion(detail)

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
            {kind && <span>{kind}</span>}
            {item.year && <span>{item.year}</span>}
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
            <MoreButton
              label={`More for ${item.title}`}
              actions={andThen(tracks.length > 0 ? queueActions(player, () => tracks, item) : [], deletion.actions)}
              disabled={tracks.length === 0 && deletion.actions.length === 0}
            />
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
                  <span className="track-title">
                    {track.title}
                    {track.trackArtist && <span className="track-artist">{track.trackArtist}</span>}
                  </span>
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
          actions={[{ label: 'Play', run: () => player.play(tracks, trackMenu.index) }, ...queueActions(player, () => [tracks[trackMenu.index]], tracks[trackMenu.index])]}
          onClose={() => setTrackMenu(null)}
        />
      )}
      {deletion.dialog}
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

/**
 * An artist and their albums, newest first, shelved by kind (see {@link albumSections}). Play and Shuffle take every
 * track they have; Radio mixes in artists like them.
 */
export function ArtistPage({ detail }: { detail: ItemDetail }) {
  const player = useAudioPlayer()
  useMusicPage()
  const { item, children: albums } = detail
  const [loading, setLoading] = useState(false)
  const deletion = useDeletion(detail)
  const navigate = useNavigate()
  // A followed artist none of whose music has arrived yet: nothing to play, but Following and what's out there.
  const waiting = albums.length === 0
  // From outside the library, when music can be requested: the albums of theirs you don't have, and artists like them.
  const extras = useApi(
    async (signal) => ((await getMusicRequestStatus(signal)).configured ? getMusicArtistExtras(item.id, signal) : undefined),
    [item.id],
  )

  // The tracks are fetched when asked for, not with the page: an artist with a hundred albums shouldn't make every
  // visit load them all.
  const playAll = async (shuffle: boolean) => {
    setLoading(true)
    try {
      const tracks = await getTracks(item.id)
      if (tracks.length > 0) player.play(shuffle ? shuffled(tracks) : tracks, 0, { openNowPlaying: true })
    } finally {
      setLoading(false)
    }
  }

  // A station the server picks: this artist and others sharing their genres, leaning towards what you play.
  const startRadio = async () => {
    setLoading(true)
    try {
      const tracks = await getRadio(item.id)
      if (tracks.length > 0) player.play(tracks, 0, { openNowPlaying: true })
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
            <span>{waiting ? 'No albums yet' : albums.length === 1 ? '1 album' : `${albums.length} albums`}</span>
          </p>
          <div className="detail-actions">
            {!waiting && (
              <>
                <AsyncButton className="button" onClick={() => playAll(false)} disabled={loading}>
                  <Play /> Play
                </AsyncButton>
                <AsyncButton className="button quiet" onClick={() => playAll(true)} disabled={loading}>
                  Shuffle
                </AsyncButton>
                <AsyncButton className="button quiet" onClick={() => startRadio()} disabled={loading}>
                  <Radio /> Radio
                </AsyncButton>
              </>
            )}
            {extras.data && (
              <MusicRequestButton
                kind="artist"
                size="large"
                request={{ kind: 'artist', artistId: item.id }}
                availability={extras.data.followed ? 'Requested' : 'None'}
                title={item.title}
                // A followed artist with nothing yet leaves the library when unfollowed: back to it.
                onUndone={waiting ? () => navigate(`/library/${item.libraryId}`, { replace: true }) : undefined}
              />
            )}
            {(!waiting || deletion.actions.length > 0) && (
              <MoreButton
                label={`More for ${item.title}`}
                actions={andThen(waiting ? [] : queueActions(player, () => getTracks(item.id), item), deletion.actions)}
              />
            )}
          </div>
        </div>
      </header>
      {albumSections(albums).map((section) => {
        const headingId = `music-section-${section.title.toLowerCase().replace(/\W+/g, '-')}`
        return (
          <section key={section.title} className="music-section" aria-labelledby={headingId}>
            <h2 id={headingId} className="music-section-title">
              {section.title}
            </h2>
            <div className="poster-grid music-albums">
              {section.albums.map((album) => (
                <PosterCard key={album.id} item={album} />
              ))}
            </div>
          </section>
        )
      })}
      {extras.data && extras.data.missing.length > 0 && (
        <section className="music-section music-missing" aria-labelledby="music-section-missing">
          <h2 id="music-section-missing" className="music-section-title">
            Not in your library
          </h2>
          <div className="poster-grid music-albums">
            {extras.data.missing.map((a, i) => (
              <MusicExternalCard key={`${i}-${a.deezerId}-${a.musicBrainzId ?? a.title}`} item={a} showArtist={false} />
            ))}
          </div>
        </section>
      )}
      {extras.data && extras.data.similar.length > 0 && (
        <Row title="Fans also like">
          {extras.data.similar.map((s, i) => (
            <MusicExternalCard key={`${i}-${s.deezerId}`} item={s} />
          ))}
        </Row>
      )}
      {deletion.dialog}
    </article>
  )
}
