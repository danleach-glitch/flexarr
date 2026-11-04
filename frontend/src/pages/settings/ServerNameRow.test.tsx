import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
import { mockApi } from '../../test/mockApi'
import { ServerNameRow } from './ServerNameRow'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
  document.title = 'Flexarr'
})

const settings = { arr: [], pathMappings: [], serverName: null as string | null, defaultServerName: 'Dans-Mac-Mini' }

const body = (api: ReturnType<typeof mockApi>) => {
  const call = api.mock.calls.filter(([, init]) => init?.method === 'PUT').at(-1)
  return call && JSON.parse(String(call[1]?.body))
}

describe('The server’s name', () => {
  it('shows the machine’s name until it’s renamed, then the new one, in the tab title too', async () => {
    const api = mockApi({ '/settings': settings, 'PUT /settings': { ...settings, serverName: 'Smith House' } })
    render(<ServerNameRow />)

    expect(await screen.findByText('Dans-Mac-Mini')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Rename the server' }))
    expect(screen.queryByRole('button', { name: 'Use the machine’s name' })).toBeNull() // already is
    fireEvent.change(screen.getByRole('textbox', { name: 'Server name' }), { target: { value: '  Smith House ' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Save' })))

    expect(body(api)).toEqual({ serverName: 'Smith House' })
    expect(await screen.findByText('Smith House')).toBeInTheDocument()
    expect(document.title).toBe('Smith House · Flexarr')
  })

  it('goes back to the machine’s name', async () => {
    const api = mockApi({ '/settings': { ...settings, serverName: 'Smith House' }, 'PUT /settings': settings })
    render(<ServerNameRow />)

    fireEvent.click(await screen.findByRole('button', { name: 'Rename the server' }))
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Use the machine’s name' })))

    expect(body(api)).toEqual({ serverName: '' })
    expect(await screen.findByText('Dans-Mac-Mini')).toBeInTheDocument()
  })

  it('says why a name was refused, and keeps the form open', async () => {
    mockApi({
      '/settings': settings,
      'PUT /settings': () => new Response(JSON.stringify({ errors: { serverName: ['Keep the name to 40 characters or fewer.'] } }), { status: 400 }),
    })
    render(<ServerNameRow />)

    fireEvent.click(await screen.findByRole('button', { name: 'Rename the server' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Server name' }), { target: { value: 'Too long' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Save' })))

    expect(await screen.findByRole('alert')).toHaveTextContent('Keep the name to 40 characters or fewer.')
    expect(screen.getByRole('textbox', { name: 'Server name' })).toBeInTheDocument()
  })

  it('isn’t there on a server that has no name', async () => {
    mockApi({ '/settings': { arr: [], pathMappings: [] } })
    const { container } = render(<ServerNameRow />)
    await act(async () => {})
    expect(container).toBeEmptyDOMElement()
  })
})
