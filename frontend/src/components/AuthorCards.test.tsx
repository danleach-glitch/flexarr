import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import type { BookAuthor } from '../api/client'
import { AuthorCards } from './AuthorCards'

const leGuin: BookAuthor = { id: 'p1', name: 'Ursula K. Le Guin', image: '/api/v1/images/people/p1?v=1', biography: 'An American author.' }
const bare: BookAuthor = { id: 'p2', name: 'A. N. Other', image: null, biography: null }

function renderCards(authors: BookAuthor[] | undefined) {
  return render(
    <MemoryRouter>
      <AuthorCards authors={authors} />
    </MemoryRouter>,
  )
}

describe('AuthorCards', () => {
  it('shows a card per author, each opening the person page', () => {
    renderCards([leGuin, { ...bare, biography: 'Wrote with her.' }])

    const links = screen.getAllByRole('link')
    expect(links.map((l) => l.getAttribute('href'))).toEqual(['/person/p1', '/person/p2'])
    expect(links[0].textContent).toContain('Ursula K. Le Guin')
    expect(links[0].querySelector('.author-card-bio')?.textContent).toBe('An American author.')
    expect(links[0].querySelector('img')?.getAttribute('src')).toContain('/api/v1/images/people/p1?v=1')
    expect(screen.getByRole('list', { name: 'Authors' })).toBeTruthy()
  })

  it('names each card by the author alone, not the biography', () => {
    renderCards([{ ...leGuin, biography: 'A very long biography. '.repeat(100) }])
    expect(screen.getByRole('link', { name: 'Ursula K. Le Guin' })).toBeTruthy()
  })

  it('keeps an author without a biography to photo and name', () => {
    renderCards([{ ...leGuin, biography: null }])
    expect(screen.getByRole('link').querySelector('.author-card-bio')).toBeNull()
    expect(screen.getByRole('list', { name: 'Author' })).toBeTruthy()
  })

  it('is hidden with no authors, none with a photo or biography, or an older server', () => {
    const { container, rerender } = renderCards(undefined)
    expect(container.innerHTML).toBe('')
    for (const authors of [[], [bare]]) {
      rerender(
        <MemoryRouter>
          <AuthorCards authors={authors} />
        </MemoryRouter>,
      )
      expect(container.innerHTML).toBe('')
    }
  })
})
