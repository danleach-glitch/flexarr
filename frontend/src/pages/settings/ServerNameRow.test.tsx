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
  it('shows the name from the status, and renames it, in the tab title too', async () => {
    const api = mockApi({ '/settings': settings, 'PUT /settings': { ...settings, serverName: 'Smith House' } })
    render(<ServerNameRow name="Dans-Mac-Mini" />)

    expect(screen.getByText('Dans-Mac-Mini')).toBeInTheDocument()
    expect(api).not.toHaveBeenCalled() // the settings wait for Rename
    fireEvent.click(screen.getByRole('button', { name: 'Rename the server' }))
    const box = screen.getByRole('textbox', { name: 'Server name' })
    await vi.waitFor(() => expect(box).toHaveAttribute('placeholder', 'Dans-Mac-Mini'))
    fireEvent.change(box, { target: { value: '  Smith House ' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Save' })))

    expect(body(api)).toEqual({ serverName: 'Smith House' })
    expect(await screen.findByText('Smith House')).toBeInTheDocument()
    expect(document.title).toBe('Smith House – Flexarr')
  })

  it('goes back to the computer’s name when the box is emptied', async () => {
    const api = mockApi({ '/settings': { ...settings, serverName: 'Smith House' }, 'PUT /settings': settings })
    render(<ServerNameRow name="Smith House" />)

    fireEvent.click(screen.getByRole('button', { name: 'Rename the server' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Server name' }), { target: { value: '' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Save' })))

    expect(body(api)).toEqual({ serverName: '' })
    expect(await screen.findByText('Dans-Mac-Mini')).toBeInTheDocument()
  })

  it('says why a name was refused, tied to the box, and keeps the form open', async () => {
    mockApi({
      '/settings': settings,
      'PUT /settings': () => new Response(JSON.stringify({ errors: { serverName: ['That name is too long.'] } }), { status: 400 }),
    })
    render(<ServerNameRow name="Dans-Mac-Mini" />)

    fireEvent.click(screen.getByRole('button', { name: 'Rename the server' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Server name' }), { target: { value: 'Too long' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Save' })))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('That name is too long.')
    const box = screen.getByRole('textbox', { name: 'Server name' })
    expect(box).toHaveAttribute('aria-invalid', 'true')
    expect(box).toHaveAttribute('aria-describedby', alert.id)
  })

  it('keeps the form while saving, even on Escape', async () => {
    let finish: (r: Response) => void = () => {}
    mockApi({ '/settings': settings, 'PUT /settings': () => new Promise<Response>((done) => (finish = done)) })
    render(<ServerNameRow name="Dans-Mac-Mini" />)

    fireEvent.click(screen.getByRole('button', { name: 'Rename the server' }))
    const box = screen.getByRole('textbox', { name: 'Server name' })
    fireEvent.change(box, { target: { value: 'Smith House' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    fireEvent.keyDown(box, { key: 'Escape' })
    expect(screen.getByRole('textbox', { name: 'Server name' })).toBeInTheDocument()

    await act(async () => finish(new Response(JSON.stringify({ ...settings, serverName: 'Smith House' }))))
    expect(await screen.findByText('Smith House')).toBeInTheDocument()
  })

  it('shows … while the status loads, and nothing on a server without names', () => {
    const { rerender, container } = render(<ServerNameRow name={undefined} />)
    expect(screen.getByText('…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Rename the server' })).toBeDisabled()
    rerender(<ServerNameRow name={null} />)
    expect(container).toBeEmptyDOMElement()
  })
})
