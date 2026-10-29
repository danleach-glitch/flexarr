import { notesParts } from '../lib/podcasts'

/**
 * An episode's notes, as text with its paragraphs, web addresses in it made links and (given `onSeek`, while it plays)
 * times in it buttons that jump there.
 */
export function EpisodeNotes({ text, duration, onSeek, className }: { text: string; duration?: number | null; onSeek?: (seconds: number) => void; className?: string }) {
  return (
    <div className={className ? `podcast-notes ${className}` : 'podcast-notes'}>
      {notesParts(text, duration).map((part, i) =>
        'url' in part ? (
          <a key={i} href={part.url} target="_blank" rel="noopener noreferrer">
            {part.url.replace(/^https?:\/\/(www\.)?/, '').replace(/\/$/, '')}
          </a>
        ) : 'time' in part && onSeek ? (
          <button key={i} type="button" className="podcast-notes-time" onClick={() => onSeek(part.time)} aria-label={`Go to ${part.text}`}>
            {part.text}
          </button>
        ) : (
          part.text
        ),
      )}
    </div>
  )
}
