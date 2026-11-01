import { useEffect, useRef } from 'react'
import { getLyrics, type Lyrics } from '../api/client'
import { useApi } from '../hooks/useApi'

export interface LyricsPanelProps {
  trackId: string
  /** Where playback is, in seconds. With synced lyrics the line being sung is highlighted and kept in view. */
  currentTime?: number
  /** The track's title, for a "Lyrics · Title" heading. Leave it out where the host has its own heading. */
  title?: string
  /** Called with a line's time when it's clicked (synced lyrics only), so the host can jump there. */
  onSeek?: (seconds: number) => void
}

/** The line being sung at `time`: the last one whose time has come, or -1 before the first. */
export function activeLine(lyrics: Lyrics | null | undefined, time: number | undefined): number {
  if (!lyrics?.synced || time === undefined) return -1
  let active = -1
  for (let i = 0; i < lyrics.lines.length; i++) {
    const at = lyrics.lines[i].time
    if (at !== null && at <= time) active = i
    else if (at !== null) break
  }
  return active
}

const reducedMotion = () => typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

/**
 * A track's lyrics, fetched for `trackId`. Renders nothing while loading or when the track has none, so a host can
 * drop it in without checking first.
 */
export function LyricsPanel({ trackId, currentTime, title, onSeek }: LyricsPanelProps) {
  const { data } = useApi((signal) => getLyrics(trackId, signal), [trackId])
  const list = useRef<HTMLOListElement>(null)
  const active = activeLine(data, currentTime)

  // The list scrolls within itself, so following the song never moves the page under the reader.
  useEffect(() => {
    const container = list.current
    const line = active >= 0 ? (container?.children[active] as HTMLElement | undefined) : undefined
    if (!container || !line) return
    const top = line.offsetTop - (container.clientHeight - line.offsetHeight) / 2
    if (typeof container.scrollTo === 'function') container.scrollTo({ top, behavior: reducedMotion() ? 'auto' : 'smooth' })
    else container.scrollTop = top
  }, [active])

  if (!data) return null

  return (
    <section className="lyrics" aria-label={title ? `Lyrics for ${title}` : 'Lyrics'}>
      {title && <h2 className="lyrics-heading">Lyrics for {title}</h2>}
      <ol ref={list} className={data.synced ? 'lyrics-lines synced' : 'lyrics-lines'}>
        {data.lines.map((line, i) => {
          const isActive = i === active
          const text = line.text || ' '
          return (
            <li key={i} className={isActive ? 'active' : undefined} aria-current={isActive || undefined}>
              {data.synced && onSeek && line.time !== null ? (
                <button type="button" onClick={() => onSeek(line.time!)}>
                  {text}
                </button>
              ) : (
                text
              )}
            </li>
          )
        })}
      </ol>
    </section>
  )
}
