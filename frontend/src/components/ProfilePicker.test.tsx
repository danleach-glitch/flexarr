import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ProfilePicker } from './ProfilePicker'
import { mockApi } from '../test/mockApi'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  localStorage.clear()
})

const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: false, hiddenLibraryIds: [], image: null }
const kids = { id: 'c1', name: 'Kids', color: '#e8a0bf', hasPin: true, isMain: false, isCurrent: false, hiddenLibraryIds: [], image: null }

const tooMany = (headers: Record<string, string>, detail = 'Too many wrong PINs. Try again in 3 seconds.') =>
  new Response(JSON.stringify({ detail }), { status: 429, headers })

describe('PIN pad', () => {
  it('takes a forgotten PIN off with the admin PIN, then chooses the profile', async () => {
    let forgotten: unknown
    const chosen = vi.fn()
    mockApi({
      'POST /profiles/c1/forgot-pin': (init?: RequestInit) => {
        forgotten = JSON.parse(String(init?.body))
        return { ...kids, hasPin: false, asksPin: false }
      },
      'POST /profiles/c1/select': { token: 'c1.sig', profile: { ...kids, hasPin: false, isCurrent: true } },
    })
    render(<ProfilePicker profiles={[main, kids]} onChosen={chosen} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Forgot PIN?' }))

    expect(screen.getByRole('heading', { name: 'Enter the admin PIN' })).toBeInTheDocument()
    for (const d of '2580') fireEvent.click(screen.getByRole('button', { name: d }))
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    await vi.waitFor(() => expect(chosen).toHaveBeenCalled())
    expect(forgotten).toEqual({ adminPin: '2580' })
  })

  it('asks for the admin PIN, under a padlock, where the server says choosing the main profile takes it', () => {
    render(<ProfilePicker profiles={[{ ...main, asksPin: true, asksAdminPin: true }, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Dan/ }))

    expect(screen.getByRole('heading', { name: 'Enter the admin PIN' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Forgot PIN?' })).not.toBeInTheDocument()
  })

  it('deletes a digit at a time, and goes back only once the PIN is empty', async () => {
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    fireEvent.click(screen.getByRole('button', { name: '1' }))
    fireEvent.click(screen.getByRole('button', { name: '2' }))
    expect(screen.getByLabelText('PIN')).toHaveValue('12')

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(screen.getByLabelText('PIN')).toHaveValue('1')
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(screen.getByLabelText('PIN')).toHaveValue('')
    expect(screen.getByRole('heading', { name: 'Enter Kids’s PIN' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(await screen.findByRole('heading', { name: 'Choose a profile' })).toBeInTheDocument()
  })

  it('goes back on Escape', async () => {
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    fireEvent.keyDown(screen.getByLabelText('PIN'), { key: 'Escape' })
    expect(await screen.findByRole('heading', { name: 'Choose a profile' })).toBeInTheDocument()
  })

  it('doesn’t take focus on a touch screen, where the phone’s own keypad would cover it', () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(pointer: coarse)' }))
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    expect(screen.getByLabelText('PIN')).not.toHaveFocus()
  })

  it('says it’s checking, and Go waits', async () => {
    let answer: (r: Response) => void = () => {}
    mockApi({ 'POST /profiles/c1/select': () => new Promise<Response>((resolve) => (answer = resolve)) })
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '1234' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    expect(await screen.findByText('Checking…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Go' })).toBeDisabled()
    await act(async () => answer(new Response(JSON.stringify({ detail: 'That PIN isn’t right.' }), { status: 403 })))
    expect(await screen.findByRole('alert')).toHaveTextContent('That PIN isn’t right.')
  })

  it('counts a lockout down with the keys off, then lets the PIN be tried again', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
    mockApi({ 'POST /profiles/c1/select': () => tooMany({ 'Retry-After': '3' }) })
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Kids/ }))
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '1234' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Go' })))

    expect(await screen.findByText('Too many wrong PINs. Try again in 3 seconds.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '5' })).toBeDisabled()
    expect(screen.getByLabelText('PIN')).toBeDisabled()
    // Not the main profile: the API key isn't a way into it.
    expect(screen.queryByRole('button', { name: 'Use the API key instead' })).not.toBeInTheDocument()

    await act(async () => vi.advanceTimersByTime(1000))
    expect(screen.getByText('Too many wrong PINs. Try again in 2 seconds.')).toBeInTheDocument()

    await act(async () => vi.advanceTimersByTime(2000))
    expect(screen.queryByText(/Too many wrong PINs/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '5' })).toBeEnabled()
    expect(screen.getByLabelText('PIN')).toBeEnabled()
  })

  it('counts down from the message when the server sends no wait, and offers the API key for the main profile', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
    mockApi({ 'POST /profiles/owner/select': () => tooMany({}, 'Too many wrong PINs. Try again in 29 seconds.') })
    render(<ProfilePicker profiles={[main, kids]} onChosen={() => {}} />)
    fireEvent.click(screen.getByRole('button', { name: /Dan/ }))
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '1234' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Go' })))

    expect(await screen.findByText('Too many wrong PINs. Try again in 29 seconds.')).toBeInTheDocument()
    await act(async () => vi.advanceTimersByTime(4000))
    expect(screen.getByText('Too many wrong PINs. Try again in 25 seconds.')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Use the API key instead' }))
    expect(screen.getByLabelText('API key')).toBeInTheDocument()
  })

  it('signs in as the main profile with the API key', async () => {
    const onChosen = vi.fn()
    let sentKey: string | null = null
    mockApi({
      'POST /session': (init?: RequestInit) => {
        sentKey = new Headers(init?.headers).get('X-Api-Key')
        return undefined
      },
      'POST /profiles/owner/select': () => tooMany({ 'Retry-After': '60' }),
    })
    localStorage.setItem('flexarr.profile', 'c1.sig')
    render(<ProfilePicker profiles={[main, kids]} onChosen={onChosen} />)
    fireEvent.click(screen.getByRole('button', { name: /Dan/ }))
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '1234' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Use the API key instead' }))
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'the-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    await vi.waitFor(() => expect(onChosen).toHaveBeenCalledWith(main))
    expect(sentKey).toBe('the-key')
    expect(localStorage.getItem('flexarr.profile')).toBeNull()
  })
})

describe('Leaving a kids profile', () => {
  it('asks for the admin PIN when the server wants it, and tries again with it', async () => {
    const mum = { ...kids, id: 'p2', name: 'Mum', hasPin: false }
    const sent: Record<string, unknown>[] = []
    mockApi({
      'POST /profiles/p2/select': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        sent.push(body)
        return body.parentPin
          ? { token: 'p2.sig', profile: { ...mum, isCurrent: true } }
          : new Response(JSON.stringify({ detail: 'Enter the admin PIN.', parentPin: true }), { status: 403 })
      },
    })
    const chosen = vi.fn()
    render(<ProfilePicker profiles={[main, mum]} onChosen={chosen} />)

    fireEvent.click(screen.getByRole('button', { name: /Mum/ }))
    expect(await screen.findByRole('heading', { name: 'Enter the admin PIN' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '2580' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    await vi.waitFor(() => expect(chosen).toHaveBeenCalled())
    expect(sent.at(-1)).toMatchObject({ parentPin: '2580' })
  })

  it('goes back to a profile’s own PIN when that was the wrong one, keeping the parent’s', async () => {
    const dad = { ...kids, id: 'p3', name: 'Dad', hasPin: true }
    const sent: Record<string, unknown>[] = []
    mockApi({
      'POST /profiles/p3/select': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        sent.push(body)
        if (!body.parentPin) return new Response(JSON.stringify({ detail: 'Choose the main profile and enter its PIN.', parentPin: true }), { status: 403 })
        if (body.pin !== '1234') return new Response(JSON.stringify({ detail: 'That PIN isn’t right.', ownPin: true }), { status: 403 })
        return { token: 'p3.sig', profile: { ...dad, isCurrent: true } }
      },
    })
    const chosen = vi.fn()
    render(<ProfilePicker profiles={[main, dad]} onChosen={chosen} />)

    fireEvent.click(screen.getByRole('button', { name: /Dad/ }))
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '9999' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))
    expect(await screen.findByRole('heading', { name: 'Enter the admin PIN' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '2580' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    expect(await screen.findByRole('heading', { name: 'Enter Dad’s PIN' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('That isn’t Dad’s PIN.')
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '1234' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))
    await vi.waitFor(() => expect(chosen).toHaveBeenCalled())
    expect(sent.at(-1)).toMatchObject({ pin: '1234', parentPin: '2580' })
  })
})
