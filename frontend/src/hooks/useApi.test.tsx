import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { useApi } from './useApi'

/** A request that answers when told to, and notices being aborted. */
function deferred<T>() {
  let answer!: (value: T) => void
  let fail!: (error: Error) => void
  const promise = new Promise<T>((resolve, reject) => {
    answer = resolve
    fail = reject
  })
  return { promise, answer, fail }
}

describe('useApi', () => {
  it('drops a slow answer for something no longer asked for', async () => {
    const first = deferred<string>()
    const second = deferred<string>()
    const signals: AbortSignal[] = []
    const { result, rerender } = renderHook(({ id }) => useApi((signal) => {
      signals.push(signal)
      return id === 'a' ? first.promise : second.promise
    }, [id]), { initialProps: { id: 'a' } })

    rerender({ id: 'b' })
    expect(signals[0].aborted).toBe(true)
    await act(async () => second.answer('B'))
    await act(async () => first.answer('A'))

    expect(result.current.data).toBe('B')
    expect(result.current.loading).toBe(false)
  })

  it('keeps what it has on screen while reloading, and says what went wrong', async () => {
    let call = 0
    const { result } = renderHook(() => useApi(async () => {
      call += 1
      if (call === 2) throw new Error('Server busy')
      return `answer ${call}`
    }, []))
    await waitFor(() => expect(result.current.data).toBe('answer 1'))

    act(() => result.current.reload())
    await waitFor(() => expect(result.current.error?.message).toBe('Server busy'))
    expect(result.current.data).toBe('answer 1')

    act(() => result.current.reload())
    await waitFor(() => expect(result.current.data).toBe('answer 3'))
    expect(result.current.error).toBeUndefined()
  })
})
