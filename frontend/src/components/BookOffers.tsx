import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock } from '../lib/format'
import { Close, Play } from './icons'

/**
 * What the player offers rather than does by itself, just above the bar: another device's place in the book, and the
 * next book in the series once this one has ended.
 */
export function BookOffers() {
  const player = useAudioPlayer()
  const { bookOffer: offer, nextBook } = player
  if (!offer && !nextBook) return null
  return (
    <div className="book-offer" role="status">
      {offer ? (
        <>
          <span>
            Jump to {formatClock(offer.position)}
            {offer.device ? ` from ${offer.device}` : ''}?
          </span>
          <button className="button small" onClick={player.acceptBookOffer}>
            Jump
          </button>
          <button className="icon-button" aria-label="Stay here" onClick={player.dismissBookOffer}>
            <Close />
          </button>
        </>
      ) : (
        nextBook && (
          <>
            <span>
              Next: <strong>{nextBook.title}</strong>
            </span>
            <button className="button small" onClick={player.playNextBook}>
              <Play /> Play
            </button>
            <button className="icon-button" aria-label="Not now" onClick={player.dismissNextBook}>
              <Close />
            </button>
          </>
        )
      )}
    </div>
  )
}
