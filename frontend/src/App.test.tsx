import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    localStorage.clear()
  })

  it('shows server status from the API', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            appName: 'Flexarr',
            version: '0.1.0',
            osDescription: 'Linux',
            runtimeVersion: '.NET 10',
            startTime: new Date().toISOString(),
          }),
        ),
      ),
    )

    render(<App />)

    expect(await screen.findByText('0.1.0')).toBeInTheDocument()
    expect(screen.getByText('.NET 10')).toBeInTheDocument()
  })

  it('reports when the server is unreachable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 503, statusText: 'Unavailable' })))

    render(<App />)

    expect(await screen.findByText(/Can't reach the server/)).toBeInTheDocument()
  })

  it('asks for an API key when the server requires one, then retries with it', async () => {
    const status = {
      appName: 'Flexarr',
      version: '0.1.0',
      osDescription: 'macOS',
      runtimeVersion: '.NET 10',
      startTime: new Date().toISOString(),
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(status)))
    vi.stubGlobal('fetch', fetchMock)

    render(<App />)
    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))

    expect(await screen.findByText('0.1.0')).toBeInTheDocument()
    expect(fetchMock.mock.lastCall?.[1]).toMatchObject({ headers: { 'X-Api-Key': 'secret' } })
  })
})
