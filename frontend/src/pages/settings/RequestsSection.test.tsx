import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { RequestDestination } from '../../api/client'
import { hasRequestChoice, requestChoices } from '../../lib/requestLibraries'
import { RequestLibrarySelect } from './RequestsSection'

const destinations: RequestDestination[] = [
  { id: 'tv', name: 'TV Shows', mediaType: 'tv', isDefault: true },
  { id: 'claire', name: 'Claire’s Shows', mediaType: 'tv', isDefault: false },
  { id: 'films', name: 'Movies', mediaType: 'movie', isDefault: true },
]

describe('where requests go', () => {
  it('offers a choice only with two libraries of a kind the profile can see', () => {
    expect(hasRequestChoice(destinations, [])).toBe(true)
    expect(hasRequestChoice(destinations, ['claire'])).toBe(false)
    expect(requestChoices(destinations, 'movie', []).map((d) => d.id)).toEqual(['films'])
  })

  it('saves a library, and Seerr’s default as no choice at all', () => {
    const onChange = vi.fn()
    render(<RequestLibrarySelect label="New shows go to" choices={requestChoices(destinations, 'tv', [])} value={null} onChange={onChange} />)
    const select = screen.getByLabelText<HTMLSelectElement>('New shows go to')
    expect(select.value).toBe('tv')

    fireEvent.change(select, { target: { value: 'claire' } })
    fireEvent.change(select, { target: { value: 'tv' } })
    expect(onChange.mock.calls).toEqual([['claire'], [null]])
  })

  it('shows Seerr’s default when the chosen library is no longer one of the choices', () => {
    render(<RequestLibrarySelect label="New shows go to" choices={requestChoices(destinations, 'tv', [])} value="gone" onChange={() => {}} />)
    expect(screen.getByLabelText<HTMLSelectElement>('New shows go to').value).toBe('tv')
  })
})
