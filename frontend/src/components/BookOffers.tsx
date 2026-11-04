import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock } from '../lib/format'
import { Close } from './icons'
import { icon } from './icons.generated'
import { t } from '../i18n'

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
            {offer.device
              ? t('Jump to {time} from {device}?', { time: formatClock(offer.position), device: offer.device })
              : t('Jump to {time}?', { time: formatClock(offer.position) })}
          </span>
          <button className="button small" onClick={player.acceptBookOffer}>
            {t('Jump')}
          </button>
          <button className="icon-button" aria-label={t('Stay here')} onClick={player.dismissBookOffer}>
            <Close />
          </button>
        </>
      ) : (
        nextBook && (
          <>
            <span>{t.rich('Next: {title}', { title: <strong>{nextBook.title}</strong> })}</span>
            <button className="button small" onClick={player.playNextBook}>
              <icon.play /> {t('Play')}
            </button>
            <button className="icon-button" aria-label={t('Not now')} onClick={player.dismissNextBook}>
              <Close />
            </button>
          </>
        )
      )}
    </div>
  )
}
