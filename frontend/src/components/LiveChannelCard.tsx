import { useState } from 'react'
import { Link } from 'react-router'
import type { LiveChannel } from '../api/client'
import { clock, programmeTime, programmeTitle, progress } from '../lib/livetv'
import { FavouriteButton } from './FavouriteButton'

/** A channel: its logo, number and what's on with how far through, then what's next; opens the channel. */
export function ChannelCard({ channel: c, favourite, onToggle }: { channel: LiveChannel; favourite: boolean; onToggle: () => void }) {
  return (
    <div className="livetv-card">
      <Link className="livetv-card-link" to={`/live/${c.id}`} aria-label={`${c.name}${c.now ? `, now ${c.now.title}` : ''}`}>
      <div className="livetv-card-head">
        <ChannelLogo channel={c} />
        <span className="livetv-card-number">{c.number}</span>
      </div>
      <div className="livetv-card-body">
        <span className="livetv-card-channel muted">{c.name}</span>
        <strong className="livetv-card-title">{c.now ? programmeTitle(c.now) : 'No listings'}</strong>
        {c.now && (
          <>
            <div className="livetv-progress" aria-hidden="true">
              <span style={{ width: `${progress(c.now) * 100}%` }} />
            </div>
            <span className="muted livetv-card-time">{programmeTime(c.now)}</span>
          </>
        )}
        {c.next && (
          <span className="muted livetv-card-next">
            Next: {clock.format(new Date(c.next.start))} {c.next.title}
          </span>
        )}
      </div>
      </Link>
      <FavouriteButton name={c.name} favourite={favourite} onToggle={onToggle} />
    </div>
  )
}

export function ChannelLogo({ channel }: { channel: Pick<LiveChannel, 'logoUrl' | 'name'> }) {
  const [failed, setFailed] = useState(false)
  return channel.logoUrl && !failed ? (
    <img className="livetv-logo" src={channel.logoUrl} alt="" loading="lazy" onError={() => setFailed(true)} />
  ) : (
    <span className="livetv-logo livetv-logo-text" aria-hidden="true">
      {channel.name.slice(0, 3)}
    </span>
  )
}
