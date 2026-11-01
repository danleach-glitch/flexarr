import { Link } from 'react-router'
import type { PodcastEpisode } from '../api/types'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { episodeAsTrack, episodeLength, episodeProgress } from '../lib/podcasts'
import { Artwork } from './Artwork'
import { Play } from './icons'

/** A podcast episode on a shelf: its artwork (with how far you got) and a play button over it, title and show beneath. */
export function EpisodeCard({ episode }: { episode: PodcastEpisode }) {
  const player = useAudioPlayer()
  const progress = episodeProgress(episode)
  return (
    <article className="card card-shell poster-card podcast-card">
      <div className="card-frame">
        <Artwork src={episode.artwork.thumb ?? episode.artwork.poster} title={episode.podcastTitle} width={342} accent={episode.accentColor} shape="square" />
        <button type="button" className="card-play" aria-label={`${progress !== null ? 'Resume' : 'Play'} ${episode.title}`} onClick={() => player.play([episodeAsTrack(episode)])}>
          <Play />
        </button>
        {progress !== null && (
          <span className="progress" aria-hidden="true">
            <span style={{ width: `${progress * 100}%` }} />
          </span>
        )}
      </div>
      <span className="card-title">
        <Link to={`/podcasts/episode/${episode.id}`}>{episode.title}</Link>
      </span>
      <span className="card-subtitle">
        <Link to={`/podcasts/${episode.podcastId}`}>{episode.podcastTitle}</Link>{'\u2003'}{episodeLength(episode)}
      </span>
    </article>
  )
}
