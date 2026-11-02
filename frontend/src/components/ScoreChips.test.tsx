import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ItemSummary } from '../api/types'
import { scoresOf, tomatoOf } from '../lib/scores'
import { summary } from '../test/mockApi'
import { Facts } from './Hero'

const movie = (overrides: Record<string, unknown>) => summary({ kind: 'Movie', title: 'Dune', ...overrides }) as unknown as ItemSummary

describe('IMDb and Rotten Tomatoes', () => {
  it('shows each score beside its mark, named for screen readers', () => {
    render(<Facts item={movie({ imdbRating: 8.1, rottenTomatoes: { score: 92, state: 'CertifiedFresh' }, criticRating: 92, communityRating: 8 })} />)
    expect(screen.getByRole('img', { name: 'IMDb 8.1 out of 10' })).toBeTruthy()
    expect(screen.getByRole('img', { name: 'Rotten Tomatoes 92%, Certified Fresh' })).toBeTruthy()
    expect(screen.queryByText('8.0')).toBeNull() // the plain score gives way to the marks
  })

  it("reads an older server's critics score as a tomato from the number", () => {
    render(<Facts item={movie({ criticRating: 41, communityRating: 6.2 })} />)
    expect(screen.getByLabelText('Rotten Tomatoes 41%, Rotten')).toBeTruthy()
    expect(screen.getByText('6.2')).toBeTruthy() // without IMDb, the plain score stays beside the tomato
  })

  it('keeps the plain score for a title with neither', () => {
    render(<Facts item={movie({ communityRating: 6.2 })} />)
    expect(screen.getByText('6.2')).toBeTruthy()
    expect(screen.queryByLabelText(/IMDb|Rotten Tomatoes/)).toBeNull()
  })

  it('follows the score when the tomato is one this app does not know', () => {
    expect(tomatoOf(75, 'SomethingNew')).toBe('Fresh')
    expect(tomatoOf(59, null)).toBe('Rotten')
    expect(tomatoOf(60)).toBe('Fresh')
    expect(scoresOf({ rottenTomatoes: { score: 88, state: 'CertifiedFresh' } }).rottenTomatoes?.state).toBe('CertifiedFresh')
    expect(scoresOf({})).toEqual({ imdb: null, rottenTomatoes: null })
  })
})
