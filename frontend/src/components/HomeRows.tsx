import type { HomeRow } from '../api/client'
import { PosterCard, Top10Card, TrackCard, WideCard } from './Cards'
import { PlaylistCard } from './PlaylistCover'
import { Row } from './Row'

/** Rows of titles as the server lays them out, for Home and each library's Browse view. */
export function HomeRows({ rows }: { rows: HomeRow[] }) {
  return rows.map((row) => (
    <Row key={row.id} title={row.title} link={row.link} style={row.style}>
      {row.style === 'top10'
        ? row.items.map((item, i) => <Top10Card key={item.id} item={item} rank={i + 1} />)
        : row.style === 'wide'
          ? row.items.map((item) => <WideCard key={item.id} item={item} />)
          : row.style === 'tracks'
            ? row.items.map((item, i) => <TrackCard key={item.id} item={item} queue={row.items} index={i} />)
            : row.style === 'playlists'
              ? (row.playlists ?? []).map((p) => <PlaylistCard key={p.id} playlist={p} />)
              : row.items.map((item) => <PosterCard key={item.id} item={item} playLoneEpisode={row.id.startsWith('recent')} />)}
    </Row>
  ))
}
