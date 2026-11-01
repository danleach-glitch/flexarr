import type { BookChapter, BookFile, BookProgress, BookSeriesRef, ItemSummary } from '../api/types'
import { formatClock } from './format'

/** A book, rather than a film, track or episode. */
export const isBook = (item: Pick<ItemSummary, 'kind'> | undefined) => item?.kind === 'Audiobook'

/** "Lee Child", "Lee Child, Andrew Child", or "Lee Child, Andrew Child and 2 more": who wrote a book, from its summary. */
export function authorsLine(item: Pick<ItemSummary, 'book'>): string | null {
  const book = item.book
  if (!book?.authors) return null
  const shown = book.authors.split(', ').length
  const more = book.authorCount - shown
  return more > 0 ? `${book.authors} and ${more} more` : book.authors
}

/** "Book 3 of 7", or "7 books" for a book without a number in its series. */
export const seriesPlace = (series: BookSeriesRef) =>
  series.sequence ? `Book ${series.sequence} of ${series.bookCount}` : series.bookCount === 1 ? '1 book' : `${series.bookCount} books`

// ---------- The timeline: a book's files back to back

/** Where a place on a book's timeline is: which file, and how far into it. */
export interface FilePlace {
  index: number
  offset: number
}

/**
 * The file holding `position` on the timeline, and the place in it: the last file starting at or before it, so a
 * position exactly on a boundary is the start of the next file. Before the start is the first file's start; past the
 * end, the last file's end.
 */
export function fileAt(files: Pick<BookFile, 'offsetSeconds' | 'durationSeconds'>[], position: number): FilePlace {
  if (files.length === 0) return { index: 0, offset: 0 }
  const at = Math.max(0, position)
  let index = 0
  for (let i = 1; i < files.length; i++) {
    if (files[i].offsetSeconds <= at) index = i
    else break
  }
  const file = files[index]
  return { index, offset: Math.min(Math.max(0, at - file.offsetSeconds), Math.max(0, file.durationSeconds)) }
}

/** A place in file `index` on the whole timeline. */
export const timelineAt = (files: Pick<BookFile, 'offsetSeconds'>[], index: number, offset: number) => (files[index]?.offsetSeconds ?? 0) + Math.max(0, offset)

// ---------- Chapters

/** The index of the chapter `position` is in (the last starting at or before it), or -1 before the first or without any. */
export function chapterIndexAt(chapters: Pick<BookChapter, 'startSeconds'>[], position: number): number {
  let found = -1
  for (let i = 0; i < chapters.length; i++) {
    if (chapters[i].startSeconds <= position) found = i
    else break
  }
  return found
}

/** Where chapter `index` ends: its own end, else where the next starts, else the end of the book. */
export function chapterEnd(chapters: BookChapter[], index: number, duration: number): number {
  const chapter = chapters[index]
  if (!chapter) return duration
  if (chapter.endSeconds > chapter.startSeconds) return chapter.endSeconds
  return chapters[index + 1]?.startSeconds ?? duration
}

/** How far through a chapter `position` is, from 0 to 1. */
export function chapterProgress(chapters: BookChapter[], index: number, position: number, duration: number): number {
  const chapter = chapters[index]
  if (!chapter) return 0
  const length = chapterEnd(chapters, index, duration) - chapter.startSeconds
  return length > 0 ? Math.min(1, Math.max(0, (position - chapter.startSeconds) / length)) : 0
}

/**
 * Where Previous chapter goes: the start of this chapter, unless it's only just begun (within `grace` seconds), when
 * it's the one before. Without chapters, the start of the book.
 */
export function previousChapterStart(chapters: BookChapter[], position: number, grace = 3): number {
  const index = chapterIndexAt(chapters, position)
  if (index < 0) return 0
  const start = chapters[index].startSeconds
  if (position - start > grace || index === 0) return start
  return chapters[index - 1].startSeconds
}

/** Where Next chapter goes: the start of the next one, or null in the last. */
export function nextChapterStart(chapters: BookChapter[], position: number): number | null {
  const next = chapters[chapterIndexAt(chapters, position) + 1]
  return next ? next.startSeconds : null
}

/** "1:02:03", with any part of a second kept ("1:02:03.5"), so a shift by half a second survives the round trip. */
export function clockText(seconds: number): string {
  const whole = Math.floor(seconds)
  const part = Math.round((seconds - whole) * 1000) / 1000
  return part > 0 ? `${formatClock(whole)}${String(part).slice(1)}` : formatClock(whole)
}

/** Reads a time typed as "1:02:03", "2:03", "123" or "12.5" (seconds); null when it isn't one. */
export function parseClock(text: string): number | null {
  const parts = text.trim().split(':')
  if (parts.length === 0 || parts.length > 3 || parts.some((p) => !/^\d+(\.\d+)?$/.test(p))) return null
  return parts.reduce((total, part) => total * 60 + Number(part), 0)
}

// ---------- Smart rewind

/** Pauses shorter than this resume where they left off. */
export const SMART_REWIND_AFTER = 10

/** The most smart rewind ever steps back, reached after an hour's pause. */
export const SMART_REWIND_MOST = 30

/**
 * How far back to start after a pause of `pausedSeconds`: nothing under 10 seconds, then from 3 seconds, growing with
 * the length of the pause to 30 after an hour (and no more after that). Whole seconds.
 */
export function smartRewindSeconds(pausedSeconds: number): number {
  if (!(pausedSeconds >= SMART_REWIND_AFTER)) return 0
  const hour = 3600
  const grown = 3 + ((SMART_REWIND_MOST - 3) * Math.log(Math.min(pausedSeconds, hour) / SMART_REWIND_AFTER)) / Math.log(hour / SMART_REWIND_AFTER)
  return Math.round(Math.min(SMART_REWIND_MOST, grown))
}

// ---------- Speed

export const SPEED_MIN = 0.5
export const SPEED_MAX = 3.5
export const SPEED_STEP = 0.05

/** A speed on the player's steps: 0.5× to 3.5× in 0.05s. */
export function clampSpeed(speed: number): number {
  if (!Number.isFinite(speed)) return 1
  const stepped = Math.round(speed / SPEED_STEP) * SPEED_STEP
  return Math.min(SPEED_MAX, Math.max(SPEED_MIN, Math.round(stepped * 100) / 100))
}

/** One step faster (1) or slower (-1). */
export const stepSpeed = (speed: number, direction: 1 | -1) => clampSpeed(clampSpeed(speed) + direction * SPEED_STEP)

/** "1×", "1.25×", "0.8×". */
export const formatSpeed = (speed: number) => `${Number(clampSpeed(speed).toFixed(2))}×`

/** The quick choices offered beside the fine steps, for books and podcasts alike. */
export const bookSpeedPresets = [0.8, 1, 1.1, 1.2, 1.25, 1.5, 1.75, 2, 2.5, 3] as const

/** The seconds skip back and forward can jump, in books and podcasts alike. */
export const skipSteps = [10, 15, 30, 45, 60] as const

/** The choices, plus a value saved elsewhere that isn't one of them (in order). */
export const withCurrent = (choices: readonly number[], current: number): number[] =>
  choices.includes(current) ? [...choices] : [...choices, current].sort((a, b) => a - b)

/** Seconds of listening left at `speed`. */
export const timeLeft = (duration: number, position: number, speed: number) => Math.max(0, duration - position) / (speed > 0 ? speed : 1)

// ---------- Sleep timer: the end of the chapter

/**
 * The volume, from 1 to 0, over the last `fade` seconds before the timer stops playback (`left` seconds of listening
 * to go): 1 until the fade starts, then down to silence.
 */
export function fadeLevel(left: number, fade: number): number {
  if (fade <= 0 || left >= fade) return 1
  return Math.max(0, left / fade)
}

/** For an end-of-chapter timer, whether `position` has reached where it stops (`at`, a place on the timeline). */
export const sleepReached = (at: number, position: number) => position >= at - 0.25

// ---------- Elsewhere: another device got further

/** What this browser last knew of its place in a book, and when (a Date.now() time). */
export interface LocalPlace {
  position: number
  savedAt: number
}

/** The server's place counts as newer only if it's at least this much newer than ours, for clocks that disagree. */
export const ELSEWHERE_GRACE_MS = 60_000

/** Places closer than this aren't worth asking about. */
export const ELSEWHERE_MIN_GAP = 30

/** Where to start a book, and a place on another device to offer instead ("Jump to 2:14:05 from Dan's iPhone?"). */
export interface StartDecision {
  start: number
  offer: { position: number; device: string | null } | null
}

/**
 * Where to start: where the server says, unless this browser has its own place and the server's is newer, from another
 * device and somewhere else in the book; then from here, offering the other place rather than jumping to it. A finished
 * book starts over. `asked` (a place chosen, like a chapter or bookmark) always wins.
 */
export function startDecision(progress: BookProgress, local: LocalPlace | null, asked?: number): StartDecision {
  const server = progress.finished ? 0 : progress.positionSeconds
  if (asked !== undefined) return { start: asked, offer: null }
  if (!local || progress.finished || !progress.updatedAt) return { start: server, offer: null }
  const updated = Date.parse(progress.updatedAt)
  const newer = Number.isFinite(updated) && updated > local.savedAt + ELSEWHERE_GRACE_MS
  if (!newer || Math.abs(server - local.position) < ELSEWHERE_MIN_GAP) return { start: newer ? server : local.position, offer: null }
  return { start: local.position, offer: { position: server, device: progress.device } }
}

const placeKey = (bookId: string) => `flexarr.bookPlace.${bookId}`

export function readLocalPlace(bookId: string): LocalPlace | null {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(placeKey(bookId)) ?? 'null')
    if (parsed && typeof parsed === 'object' && 'position' in parsed && 'savedAt' in parsed) {
      const { position, savedAt } = parsed as LocalPlace
      if (Number.isFinite(position) && Number.isFinite(savedAt)) return { position, savedAt }
    }
  } catch {
    // Unreadable or blocked: as if never played here.
  }
  return null
}

export function saveLocalPlace(bookId: string, position: number, now = Date.now()) {
  try {
    localStorage.setItem(placeKey(bookId), JSON.stringify({ position: Math.max(0, Math.floor(position)), savedAt: now }))
  } catch {
    // Storage blocked: the next start just won't know where this browser was.
  }
}

/** A name for this browser, for "Jump to … from Safari on Mac?" on the profile's other devices. */
export function deviceName(userAgent = navigator.userAgent): string {
  const browser = /Edg\//.test(userAgent)
    ? 'Edge'
    : /Firefox\//.test(userAgent)
      ? 'Firefox'
      : /Chrome\/|CriOS\//.test(userAgent)
        ? 'Chrome'
        : /Safari\//.test(userAgent)
          ? 'Safari'
          : 'A browser'
  const system = /iPhone/.test(userAgent)
    ? 'iPhone'
    : /iPad/.test(userAgent)
      ? 'iPad'
      : /Android/.test(userAgent)
        ? 'Android'
        : /Mac OS X|Macintosh/.test(userAgent)
          ? 'Mac'
          : /Windows/.test(userAgent)
            ? 'Windows'
            : /CrOS/.test(userAgent)
              ? 'Chromebook'
              : /Linux/.test(userAgent)
                ? 'Linux'
                : null
  return system ? `${browser} on ${system}` : browser
}

/** A language code ("en", "eng", "en-GB"), as opposed to a word a tag might carry instead ("English", "francais"). */
const LANGUAGE_CODE = /^[a-z]{2,3}(?:[-_][a-z0-9]{2,8})*$/i

/** "English" for "en" or "eng", where the browser knows the code; text that isn't a code (or an unknown code) as it is. */
export function languageName(code: string | null | undefined): string | null {
  if (!code) return null
  if (!LANGUAGE_CODE.test(code)) return code
  try {
    return new Intl.DisplayNames(undefined, { type: 'language' }).of(code.replaceAll('_', '-')) ?? code
  } catch {
    return code
  }
}

/**
 * The Language filter's choices: one per name, so "en" and "eng" are one "English" (its value is the one chosen, else
 * the first of its codes). The server matches a book's language exactly, so choosing it shows that code's books.
 */
export function languageOptions(codes: string[], chosen?: string): { value: string; label: string }[] {
  const byName = new Map<string, string>()
  for (const code of chosen ? [chosen, ...codes] : codes) {
    if (code === 'und') continue // a tag's way of saying the language isn't known
    const label = languageName(code) ?? code
    if (!byName.has(label)) byName.set(label, code)
  }
  return [...byName].map(([label, value]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))
}
