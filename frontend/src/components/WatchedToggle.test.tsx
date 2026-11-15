import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/client'
import { mockApi, summary } from '../test/mockApi'
import { NoticeHost } from './Notice'
import { WatchedToggle } from './WatchedToggle'

afterEach(() => vi.unstubAllGlobals())

describe('the watched tick', () => {
  it('puts itself back and says why when the server refuses', async () => {
    mockApi({ 'POST /items/m1/watched': () => Promise.reject(new TypeError('Failed to fetch')) })
    render(
      <>
        <NoticeHost />
        <WatchedToggle item={summary({ id: 'm1', title: 'Heat' }) as ItemSummary} />
      </>,
    )

    const tick = screen.getByRole('button', { name: 'Mark Heat as watched' })
    fireEvent.click(tick)
    expect(tick).toHaveAttribute('aria-pressed', 'true')

    expect(await screen.findByText('Flexarr didn’t answer. Try again.')).toBeInTheDocument()
    await waitFor(() => expect(tick).toHaveAttribute('aria-pressed', 'false'))
  })
})
