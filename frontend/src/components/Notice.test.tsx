import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { showNotice } from '../lib/notice'
import { NoticeHost } from './Notice'

afterEach(() => vi.useRealTimers())

describe('notices', () => {
  it('stay at least six seconds, longer for long ones, and while pointed at; ✕ closes them', () => {
    vi.useFakeTimers()
    const { container } = render(<NoticeHost />)
    const notice = container.firstElementChild as HTMLElement

    act(() => showNotice('Saved'))
    act(() => vi.advanceTimersByTime(5000))
    expect(screen.getByText('Saved')).toBeVisible()
    act(() => vi.advanceTimersByTime(1500))
    expect(screen.queryByText('Saved')).toBeNull()

    const long = 'Sonarr didn’t answer in time. Check that it’s running and that the address in Settings is right.'
    act(() => showNotice(long))
    act(() => vi.advanceTimersByTime(7000))
    expect(screen.getByText(long)).toBeInTheDocument()

    // Held while pointed at, however long.
    fireEvent.mouseEnter(notice)
    act(() => vi.advanceTimersByTime(60_000))
    expect(screen.getByText(long)).toBeInTheDocument()
    fireEvent.mouseLeave(notice)

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(screen.queryByText(long)).toBeNull()
  })
})
