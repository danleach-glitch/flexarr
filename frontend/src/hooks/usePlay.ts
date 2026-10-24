import { useNavigate } from 'react-router'
import { getNextUp, type ItemSummary } from '../api/client'

/** Plays a movie or episode, or the next episode of a series. */
export function usePlay() {
  const navigate = useNavigate()
  return async (item: ItemSummary) => {
    if (item.kind === 'Series') {
      const next = await getNextUp(item.id)
      navigate(next ? `/play/${next.id}` : `/item/${item.id}`)
    } else {
      navigate(`/play/${item.id}`)
    }
  }
}
