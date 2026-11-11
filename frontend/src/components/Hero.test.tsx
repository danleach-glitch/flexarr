import { act, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { summary } from '../test/mockApi'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { Hero } from './Hero'

const items = [1, 2, 3].map((n) => summary({ id: `m${n}`, kind: 'Movie', title: `Film ${n}` })) as unknown as ItemSummary[]
const shownTitle = () => screen.getByRole('heading', { level: 1 }).textContent

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  Object.defineProperty(document, 'hidden', { configurable: true, value: false })
})

describe('the home hero', () => {
  it('rotates while it can be seen, and not in a hidden tab or scrolled out of sight', () => {
    vi.useFakeTimers()
    let onSight: ((entries: { isIntersecting: boolean }[]) => void) | undefined
    vi.stubGlobal(
      'IntersectionObserver',
      class {
        constructor(callback: typeof onSight) {
          onSight = callback
        }
        observe() {}
        disconnect() {}
      },
    )
    render(
      <MemoryRouter>
        <AudioPlayerContext value={{ play: vi.fn() } as unknown as AudioPlayer}>
          <Hero items={items} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )
    expect(shownTitle()).toBe('Film 1')
    act(() => vi.advanceTimersByTime(10_000))
    expect(shownTitle()).toBe('Film 2')

    Object.defineProperty(document, 'hidden', { configurable: true, value: true })
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    act(() => vi.advanceTimersByTime(30_000))
    expect(shownTitle()).toBe('Film 2')

    Object.defineProperty(document, 'hidden', { configurable: true, value: false })
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
      onSight?.([{ isIntersecting: false }])
    })
    act(() => vi.advanceTimersByTime(30_000))
    expect(shownTitle()).toBe('Film 2')

    act(() => onSight?.([{ isIntersecting: true }]))
    act(() => vi.advanceTimersByTime(10_000))
    expect(shownTitle()).toBe('Film 3')
  })
})
