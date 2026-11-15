import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { PageError, PageLoading } from './PageStatus'

describe('PageStatus', () => {
  it('says what went wrong and offers Try again', () => {
    const retry = vi.fn()
    render(<PageError error={new Error('The server didn’t answer.')} onRetry={retry} />)
    expect(screen.getByRole('alert')).toHaveTextContent('The server didn’t answer.')
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(retry).toHaveBeenCalledOnce()
  })

  it('says the server didn’t answer, not the browser’s words, when nothing came back', () => {
    render(<PageError error={new TypeError('Failed to fetch')} onRetry={vi.fn()} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Flexarr didn’t answer. Try again.')
    expect(screen.getByRole('alert')).not.toHaveTextContent('Failed to fetch')
  })

  it('leaves Try again out when the thing is gone', () => {
    render(<PageError error={new ApiError(404, 'Not Found', [])} message="That playlist isn’t here any more." onRetry={vi.fn()} />)
    expect(screen.getByRole('alert')).toHaveTextContent('That playlist isn’t here any more.')
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  })

  it('says a bare 404 plainly, with a way home and no Try again', () => {
    render(
      <MemoryRouter>
        <PageError error={new ApiError(404, 'Not Found', [])} onRetry={vi.fn()} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('alert')).toHaveTextContent('This isn’t in your library any more.')
    expect(screen.getByRole('alert')).not.toHaveTextContent('404')
    expect(screen.getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/')
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  })

  it('shows one spinner while loading', () => {
    render(<PageLoading label="Loading channels" />)
    expect(screen.getByRole('status', { name: 'Loading channels' })).toBeInTheDocument()
  })
})
