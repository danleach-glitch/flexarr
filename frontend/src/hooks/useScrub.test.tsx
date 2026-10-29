import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { useScrub } from './useScrub'

describe('useScrub', () => {
  it('follows a drag, seeks once on letting go, and stays there until the track catches up', () => {
    const seek = vi.fn()
    const { result, rerender } = renderHook(({ time }) => useScrub(time, seek), { initialProps: { time: 100 } })

    act(() => result.current.inputProps.onChange({ target: { value: '400' } }))
    act(() => result.current.inputProps.onChange({ target: { value: '900' } }))
    expect(result.current.shown).toBe(900)
    expect(seek).not.toHaveBeenCalled()

    act(() => result.current.inputProps.onPointerUp())
    expect(seek).toHaveBeenCalledExactlyOnceWith(900)
    // The track hasn't got there yet: the slider stays where it was let go, not back at 100.
    rerender({ time: 101 })
    expect(result.current.shown).toBe(900)

    rerender({ time: 900 })
    rerender({ time: 960 })
    expect(result.current.shown).toBe(960)
  })

  it('gives up holding after a few seconds if the track never gets there', () => {
    vi.useFakeTimers()
    const { result, rerender } = renderHook(({ time }) => useScrub(time, vi.fn()), { initialProps: { time: 100 } })
    act(() => result.current.inputProps.onChange({ target: { value: '900' } }))
    act(() => result.current.inputProps.onPointerUp())
    act(() => vi.advanceTimersByTime(4000))
    rerender({ time: 101 })
    expect(result.current.shown).toBe(101)
    vi.useRealTimers()
  })
})
