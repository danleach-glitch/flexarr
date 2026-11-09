import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
import { mockApi, movies } from '../../test/mockApi'
import { LibrariesSection, WatchSwitch } from './LibrariesSection'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

describe('WatchSwitch', () => {
  it('holds its place, switched off from use, while settings load', () => {
    render(<WatchSwitch loading onChange={vi.fn()} />)
    expect((screen.getByRole('switch') as HTMLInputElement).disabled).toBe(true)
  })

  it('says so when settings cannot be read', () => {
    render(<WatchSwitch loading={false} error="Couldn’t reach the server" onChange={vi.fn()} />)
    expect(screen.queryByRole('switch')).toBeNull()
    expect(screen.getByRole('alert').textContent).toBe('Couldn’t reach the server')
  })

  it('is not there for a server that does not watch', () => {
    const { container } = render(<WatchSwitch loading={false} onChange={vi.fn()} />)
    expect(container.textContent).toBe('')
  })

  it('shows how it is set', () => {
    render(<WatchSwitch loading={false} watching={false} onChange={vi.fn()} />)
    expect((screen.getByRole('switch') as HTMLInputElement).checked).toBe(false)
  })
})

describe('Notice new files, saving', () => {
  const name = 'Notice new files straight away'
  const open = async (put: () => unknown) => {
    let saved = true
    mockApi({
      '/libraries': [movies],
      'GET /settings': () => ({ watchFolders: saved }),
      'PUT /settings': async (init?: RequestInit) => {
        const answer = await put()
        if (answer instanceof Response) return answer
        saved = (JSON.parse(String(init?.body)) as { watchFolders: boolean }).watchFolders
        return answer ?? { watchFolders: saved }
      },
    })
    render(
      <MemoryRouter>
        <LibrariesSection />
      </MemoryRouter>,
    )
    const row = await screen.findByText(name)
    const box = row.closest('label')!.querySelector('input')!
    await waitFor(() => expect(box).toBeEnabled())
    await waitFor(() => expect(box).toBeChecked())
    return box
  }

  it('moves straight away, before the save has answered', async () => {
    let answer: ((value?: unknown) => void) | undefined
    const box = await open(() => new Promise((resolve) => (answer = resolve)))

    fireEvent.click(box)

    expect(box).not.toBeChecked()
    expect(box).toBeEnabled()
    await waitFor(() => expect(answer).toBeDefined())
    answer?.()
    await waitFor(() => expect(box).not.toBeChecked())
  })

  it('goes back, and says why, when it can’t save', async () => {
    let answer: ((value: unknown) => void) | undefined
    const box = await open(() => new Promise((resolve) => (answer = resolve)))

    fireEvent.click(box)
    expect(box).not.toBeChecked()
    await waitFor(() => expect(answer).toBeDefined())
    answer?.(new Response(JSON.stringify({ title: 'Not today' }), { status: 500 }))

    await waitFor(() => expect(box).toBeChecked())
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })

  it('saves flicks in order, ending on the last', async () => {
    const answers: ((value?: unknown) => void)[] = []
    const box = await open(() => new Promise((resolve) => answers.push(resolve)))
    const bodies = () =>
      vi
        .mocked(fetch)
        .mock.calls.filter(([input, init]) => String(input).endsWith('/settings') && init?.method === 'PUT')
        .map(([, init]) => JSON.parse(String(init?.body)) as unknown)

    fireEvent.click(box)
    fireEvent.click(box)
    expect(box).toBeChecked()
    await waitFor(() => expect(answers).toHaveLength(1))
    // The second waits for the first.
    await new Promise((done) => setTimeout(done, 20))
    expect(answers).toHaveLength(1)
    expect(bodies()).toEqual([{ watchFolders: false }])

    answers[0]()
    await waitFor(() => expect(answers).toHaveLength(2))
    expect(box).toBeChecked()
    answers[1]()
    await waitFor(() => expect(box).toBeChecked())
    // Off, then on: the server ends where the switch does.
    expect(bodies()).toEqual([{ watchFolders: false }, { watchFolders: true }])
  })
})
