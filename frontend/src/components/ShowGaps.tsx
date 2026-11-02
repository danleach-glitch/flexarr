import { useState } from 'react'
import { ApiError, imageUrl, searchMissing, type ShowEpisode, type ShowMissing } from '../api/client'
import { airDay } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { RequestButton } from './RequestButton'

const code = (e: ShowEpisode) => `S${e.seasonNumber}E${e.episodeNumber}`

/** Asks Sonarr to look for a season's missing episodes, then says it did. */
function SearchButton({ showId, season, label }: { showId: string; season: number; label: string }) {
  const [state, setState] = useState<'idle' | 'sent' | string>('idle')
  if (state === 'sent') return <span className="external-state">Searching</span>
  return (
    <AsyncButton
      className="button small quiet"
      aria-label={label}
      title={state === 'idle' ? undefined : state}
      onClick={async () => {
        try {
          await searchMissing(showId, season)
          setState('sent')
        } catch (e) {
          setState(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The search didn’t go through.')
        }
      }}
    >
      {state === 'idle' ? 'Search' : 'Try again'}
    </AsyncButton>
  )
}

/**
 * Below a show's seasons: the episodes coming next, then what Sonarr (or Seerr) knows that the library doesn't, with
 * Request for a whole season (Seerr) and Search for a season's missing episodes (Sonarr, main profile).
 */
export function ShowGaps({ showId, tmdbId, gaps }: { showId: string; tmdbId: number | null; gaps: ShowMissing }) {
  const bySeason = new Map<number, ShowEpisode[]>()
  for (const e of gaps.episodes) bySeason.set(e.seasonNumber, [...(bySeason.get(e.seasonNumber) ?? []), e])
  const hasMissing = gaps.seasons.length > 0 || gaps.episodes.length > 0

  return (
    <>
      {gaps.upcoming.length > 0 && (
        <section className="show-gaps" aria-labelledby="show-coming-up">
          <h2 id="show-coming-up">Coming up</h2>
          <ul className="gap-episodes">
            {gaps.upcoming.map((e) => (
              <li key={code(e)}>
                <span className="gap-code">{code(e)}</span>
                <span className="gap-title">{e.title ?? `Episode ${e.episodeNumber}`}</span>
                {e.airDate && <span className="muted">Airs {airDay(e.airDate)}</span>}
              </li>
            ))}
          </ul>
        </section>
      )}
      {hasMissing && (
        <section className="show-gaps" aria-labelledby="show-missing">
          <h2 id="show-missing">Not in your library</h2>
          {gaps.seasons.length > 0 && (
            <div className="poster-grid gap-seasons">
              {gaps.seasons.map((s) => {
                const name = s.name ?? `Season ${s.seasonNumber}`
                return (
                  <div key={s.seasonNumber} className="card external-card">
                    <div className="card-frame">
                      <div className="artwork artwork-poster">
                        {s.poster ? <img src={imageUrl(s.poster, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{name}</span>}
                      </div>
                    </div>
                    <span className="card-title">{name}</span>
                    <span className="card-subtitle">{[`${s.episodes} ${s.episodes === 1 ? 'episode' : 'episodes'}`, s.year].filter(Boolean).join(', ')}</span>
                    <div className="external-card-actions">
                      {gaps.canRequest && tmdbId != null ? (
                        <RequestButton title={{ mediaType: 'tv', tmdbId, availability: s.availability, libraryItemId: null }} seasons={[s.seasonNumber]} label={`Request ${name}`} name={name} />
                      ) : s.availability === 'Requested' ? (
                        <span className="external-state">Requested</span>
                      ) : gaps.canSearch ? (
                        <SearchButton showId={showId} season={s.seasonNumber} label={`Search for ${name}`} />
                      ) : null}
                    </div>
                  </div>
                )
              })}
            </div>
          )}
          {[...bySeason].map(([season, episodes]) => (
            <div key={season} className="gap-season">
              <div className="gap-season-head">
                <h3>
                  Season {season}: {episodes.length} {episodes.length === 1 ? 'episode' : 'episodes'}
                </h3>
                {gaps.canSearch && <SearchButton showId={showId} season={season} label={`Search for season ${season}’s missing episodes`} />}
              </div>
              <ul className="gap-episodes">
                {episodes.map((e) => (
                  <li key={code(e)}>
                    <span className="gap-code">{code(e)}</span>
                    <span className="gap-title">{e.title ?? `Episode ${e.episodeNumber}`}</span>
                    {e.airDate && <span className="muted">{airDay(e.airDate)}</span>}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </section>
      )}
    </>
  )
}
