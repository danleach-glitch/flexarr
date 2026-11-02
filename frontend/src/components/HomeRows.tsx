import type { HomeRow, UpcomingItem } from '../api/client'
import { useFavourites } from '../hooks/useFavourites'
import { PosterCard, Top10Card, TrackCard, WideCard } from './Cards'
import { ChannelCard } from './LiveChannelCard'
import { MixCard } from './MixCard'
import { PlaylistCard } from './PlaylistCover'
import { Row } from './Row'
import { UpcomingCard } from './UpcomingCard'

/**
 * Rows of titles as the server lays them out, for Home and each library's Browse view. Coming soon's titles travel
 * beside the rows, in `upcoming`: without any, its row is left out.
 */
export function HomeRows({ rows, upcoming = [] }: { rows: HomeRow[]; upcoming?: UpcomingItem[] }) {
  const { isFavourite, toggle } = useFavourites()
  return rows.map((row) =>
    row.style === 'upcoming' ? (
      upcoming.length > 0 && (
        <Row key={row.id} title="Coming soon" link="/upcoming" style="wide">
          {upcoming.map((item) => (
            <UpcomingCard key={item.id} item={item} />
          ))}
        </Row>
      )
    ) : (
      <Row key={row.id} title={row.title} link={row.link} style={row.style}>
      {row.style === 'top10'
        ? row.items.map((item, i) => <Top10Card key={item.id} item={item} rank={i + 1} />)
        : row.style === 'wide'
          ? row.items.map((item) => <WideCard key={item.id} item={item} continueWatching={row.id === 'continue'} />)
          : row.style === 'tracks'
            ? row.items.map((item, i) => <TrackCard key={item.id} item={item} queue={row.items} index={i} />)
            : row.style === 'playlists'
              ? (row.playlists ?? []).map((p) => <PlaylistCard key={p.id} playlist={p} />)
              : row.style === 'mixes'
                ? (row.mixes ?? []).map((m) => <MixCard key={m.id} mix={m} />)
                : row.style === 'live'
                  ? (row.channels ?? []).map((c) => <ChannelCard key={c.id} channel={c} favourite={isFavourite(c)} onToggle={() => toggle(c)} />)
                  : row.items.map((item) => <PosterCard key={item.id} item={item} playLoneEpisode={row.id.startsWith('recent')} />)}
    </Row>
    ),
  )
}
