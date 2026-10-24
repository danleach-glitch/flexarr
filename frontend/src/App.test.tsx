import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

describe('App', () => {
  afterEach(() => vi.unstubAllGlobals())

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
})
