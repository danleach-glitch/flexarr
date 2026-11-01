import { memo, use, useCallback, useState, type CSSProperties } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  clearApiCache,
  deleteBookmark,
  getBookListening,
  imageUrl,
  listenAgain,
  setBookFinished,
  unlockFirst,
  type BookChapter,
  type Bookmark,
  type Credit,
  type ItemDetail,
  type ItemSummary,
} from '../api/client'
import { openAddToCollection } from '../components/AddToCollection'
import { Artwork } from '../components/Artwork'
import { BookEditor } from '../components/BookEditor'
import { AsyncButton } from '../components/AsyncButton'
import { AuthorCards } from '../components/AuthorCards'
import { PosterCard } from '../components/Cards'
import { ChapterEditor } from '../components/ChapterEditor'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { DeleteDialog } from '../components/DeleteDialog'
import { FixMatchDialog } from '../components/FixMatchDialog'
import { Bookmark as BookmarkIcon, Check, Close, Pause, Play } from '../components/icons'
import type { MenuAction } from '../components/ItemMenu'
import { Overview } from '../components/Overview'
import { Row } from '../components/Row'
import { AudioTimeContext, useAudioPlayer } from '../hooks/audioPlayerContext'
import { announceBookmarks, useBookmarkChanges } from '../hooks/bookmarks'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { announceWatched } from '../hooks/watched'
import { chapterIndexAt, chapterProgress, formatSpeed, languageName, seriesPlace, timeLeft } from '../lib/books'
import { formatClock, formatDuration } from '../lib/format'
import { MoreButton } from './MusicPages'

/**
 * A book's page: cover, title, its series and people, how long it is and how long is left at the speed it plays at,
 * its chapters (with where you are), bookmarks, and more from its series, author and narrator.
 *
 * Only the parts that show where the book has got to follow the player's time, and only while this book is the one
 * playing, so the page itself doesn't redraw every second while it (or anything else) plays.
 */
export function BookPage({ detail, onChange }: { detail: ItemDetail; onChange: () => void }) {
  const { item } = detail
  const book = detail.book
  const player = useAudioPlayer()
  const navigate = useNavigate()
  const { me } = useSession()
  const listening = useApi(getBookListening, [])
  const [deleting, setDeleting] = useState(false)
  const [editing, setEditing] = useState<'details' | 'chapters' | 'match' | null>(null)
  const [startingOver, setStartingOver] = useState(false)
  // A bookmark added or removed in the player shows here too.
  // Its page is kept a while: fetched afresh, not from there, so the change shows straight away.
  const bookmarksChanged = useCallback(() => {
    clearApiCache((path) => path.startsWith(`/items/${item.id}`))
    onChange()
  }, [item.id, onChange])
  useBookmarkChanges(item.id, bookmarksChanged)

  if (!book) return <p className="page-message muted">This book can’t be shown by this version of the app.</p>

  const playing = player.current?.id === item.id
  const duration = book.durationSeconds || item.durationSeconds || 0
  const finished = item.userData.watched
  const place: Place = { playing, saved: finished ? 0 : item.userData.positionSeconds }
  const speed = playing ? player.speed : (book.speed ?? listening.data?.speed ?? 1)
  const authors = detail.credits.filter((c) => c.role === 'Author')
  const narrators = detail.credits.filter((c) => c.role === 'Narrator')
  const translators = detail.credits.filter((c) => c.role === 'Translator')
  const series = book.series[0]
  const mainProfile = me?.isAdmin === true || me?.canUnlock === true

  /** Plays from `at` (a chapter, a bookmark), or seeks there when the book is already playing. */
  const playAt = (at?: number) => {
    if (playing && at !== undefined) {
      player.seek(at)
      if (!player.playing) player.toggle()
    } else if (playing) player.toggle()
    else player.play([{ ...item, playFrom: at }])
  }

  /** Through the player while this book plays, so its own list of bookmarks keeps up. */
  const removeBookmark = (id: string) => (playing ? player.removeBookmark(id) : deleteBookmark(id).then(() => announceBookmarks(item.id)))

  async function mark(to: boolean) {
    await setBookFinished(item.id, to)
    announceWatched(item)
    onChange()
  }

  async function again() {
    await listenAgain(item.id)
    announceWatched(item)
    player.play([{ ...item, playFrom: 0, userData: { ...item.userData, watched: false, positionSeconds: 0 } }])
    onChange()
  }

  const more: MenuAction[] = [{ label: 'Add to collection…', run: () => openAddToCollection(item) }]
  // A finished book starts over straight from its button; one part-way through asks first, since its place is lost.
  if ((playing || place.saved > 0) && !finished) more.push({ label: 'Start over', run: () => setStartingOver(true) })
  // Unread: no place and not finished, as if never started. Not while it plays, which would set the place again.
  if (!playing && (finished || place.saved > 0)) more.push({ label: 'Mark as unread', run: () => mark(false) })
  if (mainProfile) {
    // Saving takes the admin: the PIN first, if it's run out here.
    more.push({ label: 'Edit…', run: () => void unlockFirst().then((unlocked) => unlocked && setEditing('details')) })
    more.push({ label: 'Edit chapters…', run: () => setEditing('chapters') })
    // Its search is a read the server keeps to the unlocked main profile, so a locked one doesn't get to ask for the PIN.
    if (me?.isAdmin === true) more.push({ label: 'Fix match…', run: () => setEditing('match') })
    more.push('divider', { label: 'Delete…', run: () => setDeleting(true), danger: true })
  }

  // "und" is a tag's way of saying the language isn't known.
  const edition = [book.publisher, book.language === 'und' ? null : languageName(book.language)].filter((e): e is string => !!e)
  // The same test AuthorCards makes: an author with a photo or a biography (servers before 0.58 send none).
  const hasAuthorCards = !!book.authors?.some((a) => a.image || a.biography)

  return (
    <article className="music-page book-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          {series && (
            <p className="book-series-link">
              <Link to={`/book-series/${series.id}`}>{series.name}</Link>
              <span className="muted">{seriesPlace(series)}</span>
            </p>
          )}
          <h1>{item.title}</h1>
          {book.subtitle && <p className="book-subtitle">{book.subtitle}</p>}
          <People label="By" people={authors} />
          <People label="Read by" people={narrators} />
          <People label="Translated by" people={translators} />
          <Facts place={place} year={item.year} duration={duration} finished={finished} speed={speed} abridged={book.abridged} />
          <div className="detail-actions">
            {finished ? (
              <AsyncButton className="button" onClick={again}>
                <Play /> Start over
              </AsyncButton>
            ) : (
              <PlayButton place={place} sounding={playing && player.playing} onClick={() => playAt()} />
            )}
            <AsyncButton className="button quiet" aria-pressed={finished} onClick={() => mark(!finished)}>
              <Check /> {finished ? 'Finished' : 'Mark finished'}
            </AsyncButton>
            <MoreButton label={`More for ${item.title}`} actions={more} />
          </div>
        </div>
      </header>

      <div className="book-body">
        {(detail.overview || hasAuthorCards) && (
          <section className="book-about">
            {detail.overview && <Overview text={detail.overview} />}
            {detail.overview && edition.length > 0 && (
              <p className="muted book-edition">
                {edition.map((e) => (
                  <span key={e}>{e}</span>
                ))}
              </p>
            )}
            <AuthorCards authors={book.authors} />
          </section>
        )}
        {book.chapters.length > 0 && (
          <section className="book-chapters">
            <h2>{book.chapters.length === 1 ? '1 chapter' : `${book.chapters.length} chapters`}</h2>
            <Chapters
              chapters={book.chapters}
              bookmarks={book.bookmarks}
              place={place}
              sounding={playing && player.playing}
              duration={duration}
              onPlay={playAt}
              onRemoveBookmark={removeBookmark}
            />
          </section>
        )}
      </div>

      <div className="book-rows">
        <BookRow title={series ? `More in ${series.name}` : 'More in the series'} items={book.moreInSeries} link={series ? `/book-series/${series.id}` : undefined} />
        <BookRow title={authors[0] ? `More by ${authors[0].name}` : 'More by the author'} items={book.byAuthor} link={authors[0] ? `/person/${authors[0].personId}` : undefined} />
        <BookRow title={narrators[0] ? `Read by ${narrators[0].name}` : 'Read by the narrator'} items={book.readByNarrator} link={narrators[0] ? `/person/${narrators[0].personId}` : undefined} />
      </div>

      {startingOver && (
        <StartOverDialog
          place={place}
          onConfirm={() => {
            setStartingOver(false)
            playAt(0)
          }}
          onClose={() => setStartingOver(false)}
        />
      )}
      {editing === 'details' && <BookEditor detail={detail} onSaved={onChange} onClose={() => setEditing(null)} />}
      {editing === 'chapters' && <ChapterEditor bookId={item.id} chapters={book.chapters} hasAsin={!!book.asin} onSaved={onChange} onClose={() => setEditing(null)} />}
      {editing === 'match' && <FixMatchDialog item={item} onFixed={onChange} onClose={() => setEditing(null)} />}
      {deleting && <DeleteDialog item={item} onClose={() => setDeleting(false)} onDeleted={() => navigate(`/library/${item.libraryId}`, { replace: true })} />}
    </article>
  )
}

/** Whether the book is the one playing, and where it was left. */
interface Place {
  playing: boolean
  saved: number
}

/**
 * Where the book has got to: the player's time while it plays, else where it was left. The time is only read (`use`
 * may be skipped) while this book is playing, so another book or a song playing doesn't redraw the page each second.
 */
function usePosition({ playing, saved }: Place) {
  return playing ? use(AudioTimeContext) : saved
}

function Facts({ place, year, duration, finished, speed, abridged }: { place: Place; year: number | null; duration: number; finished: boolean; speed: number; abridged: boolean }) {
  const position = usePosition(place)
  const facts = [
    year?.toString(),
    formatDuration(duration),
    position > 0 && !finished ? `${formatDuration(timeLeft(duration, position, speed))} left${speed !== 1 ? ` at ${formatSpeed(speed)}` : ''}` : null,
    abridged ? 'Abridged' : null,
  ].filter(Boolean)
  return (
    <p className="facts">
      {facts.map((f) => (
        <span key={f}>{f}</span>
      ))}
    </p>
  )
}

function PlayButton({ place, sounding, onClick }: { place: Place; sounding: boolean; onClick: () => void }) {
  const position = usePosition(place)
  return (
    <button className="button" onClick={onClick}>
      {sounding ? <Pause /> : <Play />} {sounding ? 'Pause' : position > 0 ? `Resume from ${formatClock(position)}` : 'Play'}
    </button>
  )
}

/** Asks before losing the place: "Start over? You're at 1:23:04." */
function StartOverDialog({ place, onConfirm, onClose }: { place: Place; onConfirm: () => void; onClose: () => void }) {
  const position = usePosition(place)
  return (
    <ConfirmDialog title="Start over?" confirmLabel="Start over" onConfirm={onConfirm} onClose={onClose}>
      <p>You’re at {formatClock(position)}.</p>
    </ConfirmDialog>
  )
}

/**
 * The chapter list. Only the chapter playing and whether it's been passed follow the time, so the list itself redraws
 * when the chapter changes; the bar in the chapter playing (`ChapterBar`) follows each second.
 */
function Chapters({
  chapters,
  bookmarks,
  place,
  sounding,
  duration,
  onPlay,
  onRemoveBookmark,
}: {
  chapters: BookChapter[]
  bookmarks: Bookmark[]
  place: Place
  sounding: boolean
  duration: number
  onPlay: (at: number) => void
  onRemoveBookmark: (id: string) => Promise<unknown>
}) {
  const position = usePosition(place)
  const current = position > 0 ? chapterIndexAt(chapters, position) : -1
  const end = chapters[current]?.endSeconds ?? 0
  const currentDone = end > 0 && position >= end
  return (
    <ChapterList
      chapters={chapters}
      bookmarks={bookmarks}
      place={place}
      current={current}
      currentDone={currentDone}
      sounding={sounding}
      duration={duration}
      onPlay={onPlay}
      onRemoveBookmark={onRemoveBookmark}
    />
  )
}

const ChapterList = memo(function ChapterList({
  chapters,
  bookmarks,
  onRemoveBookmark,
  place,
  current,
  currentDone,
  sounding,
  duration,
  onPlay,
}: {
  chapters: BookChapter[]
  /** Each shows under the chapter it's in. */
  bookmarks: Bookmark[]
  onRemoveBookmark: (id: string) => Promise<unknown>
  place: Place
  /** The chapter the place is in, or -1 before the book's begun. */
  current: number
  /** The place is past the current chapter's end (a gap before the next). */
  currentDone: boolean
  sounding: boolean
  duration: number
  onPlay: (at: number) => void
}) {
  const marks = [...bookmarks].sort((a, b) => a.positionSeconds - b.positionSeconds)
  return (
    <ol className="tracklist">
      {chapters.map((chapter, i) => {
        const inChapter = marks.filter((b) => chapterIndexAt(chapters, b.positionSeconds) === i)
        // Chapters before the one playing are behind the place; the one playing only once the place is past its end.
        const done = chapter.endSeconds > 0 && (i < current || (i === current && currentDone))
        const here = i === current
        return (
          <li key={chapter.index}>
            <div className="track-row">
              <button className={here ? 'track current' : done ? 'track done' : 'track'} onClick={() => onPlay(chapter.startSeconds)} aria-current={here || undefined}>
                <span className="track-number">{here && sounding ? <Play /> : done ? <Check aria-label="Heard" /> : i + 1}</span>
                <span className="track-title">
                  {chapter.title}
                  {here && <ChapterBar chapters={chapters} index={i} place={place} duration={duration} />}
                </span>
                <span className="track-time">{formatClock(chapter.endSeconds - chapter.startSeconds)}</span>
              </button>
            </div>
            {inChapter.length > 0 && (
              <ul className="chapter-bookmarks" aria-label={`Bookmarks in ${chapter.title}`}>
                {inChapter.map((b) => (
                  <li key={b.id} className="chapter-bookmark">
                    <button className="chapter-bookmark-jump" onClick={() => onPlay(b.positionSeconds)}>
                      <BookmarkIcon />
                      <span className="chapter-bookmark-time">{formatClock(b.positionSeconds)}</span>
                      {b.note && <span className="chapter-bookmark-note">{b.note}</span>}
                    </button>
                    <AsyncButton className="icon-button" aria-label={`Remove the bookmark at ${formatClock(b.positionSeconds)}`} onClick={() => onRemoveBookmark(b.id)}>
                      <Close />
                    </AsyncButton>
                  </li>
                ))}
              </ul>
            )}
          </li>
        )
      })}
    </ol>
  )
})

/** How far into the chapter playing the place is: the one part of the list that follows the time each second. */
function ChapterBar({ chapters, index, place, duration }: { chapters: BookChapter[]; index: number; place: Place; duration: number }) {
  const position = usePosition(place)
  return <span className="chapter-progress" style={{ width: `${chapterProgress(chapters, index, position, duration) * 100}%` }} />
}

function People({ label, people }: { label: string; people: Credit[] }) {
  if (people.length === 0) return null
  return (
    <p className="book-people">
      <span className="muted">{label} </span>
      {people.map((p, i) => (
        <span key={p.personId}>
          {i > 0 && (i === people.length - 1 ? ' and ' : ', ')}
          <Link to={`/person/${p.personId}`}>{p.name}</Link>
        </span>
      ))}
    </p>
  )
}

function BookRow({ title, items, link }: { title: string; items: ItemSummary[]; link?: string }) {
  if (items.length === 0) return null
  return (
    <Row title={title} link={link}>
      {items.map((b) => (
        <PosterCard key={b.id} item={b} />
      ))}
    </Row>
  )
}
