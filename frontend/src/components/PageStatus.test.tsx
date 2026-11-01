import { fireEvent, render, screen } from '@testing-library/react'
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

  it('leaves Try again out when the thing is gone', () => {
    render(<PageError error={new ApiError(404, 'Not Found', [])} message="That playlist isn’t here any more." onRetry={vi.fn()} />)
    expect(screen.getByRole('alert')).toHaveTextContent('That playlist isn’t here any more.')
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
  })

  it('shows one spinner while loading', () => {
    render(<PageLoading label="Loading channels" />)
    expect(screen.getByRole('status', { name: 'Loading channels' })).toBeInTheDocument()
  })
})
