import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { Shell } from '../App'
import { detail, mockApi, summary } from '../test/mockApi'

// The order every client shares (docs/design.md › Platforms): Play, Start over, Shuffle, Trailer, Watched, Watchlist, ⋯.
describe('Detail page actions', () => {
  it('puts Start over beside Play once a movie is part-watched, in the shared order', async () => {
    const heat = summary({ id: 'm9', title: 'Heat', durationSeconds: 10_000, userData: { positionSeconds: 1_930, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null } })
    mockApi({ '/items/m9': detail(heat, { trailerYouTubeId: 'dQw4w9WgXcQ', onWatchlist: false }), '/items/m9/similar': [] })
    render(
      <MemoryRouter initialEntries={['/item/m9']}>
        <Shell />
      </MemoryRouter>,
    )
    await screen.findByRole('button', { name: /Resume from 32:10/ })
    const actions = screen.getByRole('button', { name: /Resume from/ }).closest('.detail-actions') as HTMLElement
    const names = Array.from(actions.querySelectorAll('button')).map((b) => b.getAttribute('aria-label') ?? b.textContent?.trim())
    expect(names.slice(0, 4)).toEqual(['Resume from 32:10', 'Start over', 'Trailer', 'Mark as watched'])
    expect(names.at(-1)).toBe('More actions')
  })

  it('has no Start over before anything has been watched', async () => {
    const heat = summary({ id: 'm8', title: 'Heat' })
    mockApi({ '/items/m8': detail(heat), '/items/m8/similar': [] })
    render(
      <MemoryRouter initialEntries={['/item/m8']}>
        <Shell />
      </MemoryRouter>,
    )
    await screen.findByRole('button', { name: 'Play' })
    expect(screen.queryByRole('button', { name: 'Start over' })).not.toBeInTheDocument()
  })
})
