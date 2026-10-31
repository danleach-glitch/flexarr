import { useNavigate } from 'react-router'
import { getNextUp, type ItemSummary } from '../api/client'
import { useAudioPlayer } from './audioPlayerContext'

/** Plays a movie or episode, or the next episode of a series; a book plays in the audio player, where you are. */
export function usePlay() {
  const navigate = useNavigate()
  const audio = useAudioPlayer()
  return async (item: ItemSummary) => {
    if (item.kind === 'Audiobook') {
      audio.play([item])
    } else if (item.kind === 'Series') {
      const next = await getNextUp(item.id)
      navigate(next ? `/play/${next.id}` : `/item/${item.id}`)
    } else {
      navigate(`/play/${item.id}`)
    }
  }
}
