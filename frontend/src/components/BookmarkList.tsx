import type { BookChapter, Bookmark } from '../api/types'
import { chapterIndexAt } from '../lib/books'
import { formatClock } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { Close } from './icons'
import { t } from '../i18n'

/** Bookmarks in a book, in order: each a place (and its chapter) to jump to, its note, and a way to remove it. */
export function BookmarkList({
  id,
  bookmarks,
  chapters,
  onJump,
  onRemove,
}: {
  id?: string
  bookmarks: Bookmark[]
  chapters: BookChapter[]
  onJump: (at: number) => void
  onRemove: (id: string) => Promise<unknown>
}) {
  return (
    <ol className="book-bookmarks" id={id} aria-label={t('Bookmarks')}>
      {[...bookmarks]
        .sort((a, b) => a.positionSeconds - b.positionSeconds)
        .map((b) => {
          const chapter = chapters[chapterIndexAt(chapters, b.positionSeconds)]
          return (
            <li key={b.id} className="book-bookmark">
              <button className="book-bookmark-jump" onClick={() => onJump(b.positionSeconds)}>
                <span className="book-bookmark-time">{formatClock(b.positionSeconds)}</span>
                <span className="book-bookmark-text">
                  {b.note ?? chapter?.title ?? t('Bookmark')}
                  {b.note && chapter && <span className="muted">{chapter.title}</span>}
                </span>
              </button>
              <AsyncButton className="icon-button" aria-label={t('Remove the bookmark at {time}', { time: formatClock(b.positionSeconds) })} onClick={() => onRemove(b.id)}>
                <Close />
              </AsyncButton>
            </li>
          )
        })}
    </ol>
  )
}
