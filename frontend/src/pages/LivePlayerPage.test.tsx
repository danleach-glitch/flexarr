import { act, render } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import LivePlayerPage from './LivePlayerPage'

beforeAll(() => {
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined)
  window.HTMLMediaElement.prototype.pause = vi.fn()
})

afterEach(() => vi.unstubAllGlobals())

describe('Live player', () => {
  it('lets a channel still starting go when the page is left', async () => {
    let answer: (value: unknown) => void = () => undefined
    const stopped: string[] = []
    mockApi({
      '/livetv/channels': [],
      'POST /livetv/channels/c1/watch': () => new Promise((resolve) => (answer = resolve)),
      'DELETE /livetv/watch/v1/': (_: RequestInit | undefined, url: URL) => {
        stopped.push(url.pathname)
        return undefined
      },
    })
    const page = render(
      <MemoryRouter initialEntries={['/live/c1']}>
        <Routes>
          <Route path="live/:id" element={<LivePlayerPage />} />
        </Routes>
      </MemoryRouter>,
    )
    // Back, before the server has answered.
    page.unmount()
    await act(async () => {
      answer({ viewer: 'v1', playlist: '/live.m3u8', channel: { id: 'c1', name: 'One', number: 1 } })
      await new Promise((resolve) => setTimeout(resolve, 10))
    })
    expect(stopped).toEqual(['/api/v1/livetv/watch/v1/'])
  })
})
