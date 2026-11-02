import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, type ExternalTitle } from '../api/client'
import { RequestButton } from './RequestButton'

// Plain functions rather than vi.fn: vitest's mock leaves a rejected call's promise unhandled, which fails the test.
const sent: unknown[] = []
let refusal: Error | undefined
let status = { configured: true, reachable: true }
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  requestTitle: async (...args: unknown[]) => {
    sent.push({ request: args })
    if (refusal) throw refusal
  },
  unrequestTitle: async (...args: unknown[]) => {
    sent.push({ unrequest: args })
    if (refusal) throw refusal
  },
  getRequestStatusCached: async () => status,
}))

const film: Pick<ExternalTitle, 'mediaType' | 'tmdbId' | 'availability' | 'libraryItemId' | 'title'> = {
  mediaType: 'movie',
  tmdbId: 603,
  availability: 'None',
  libraryItemId: null,
  title: 'The Matrix',
}

const renderButton = (props: Parameters<typeof RequestButton>[0]) =>
  render(
    <MemoryRouter>
      <RequestButton {...props} />
    </MemoryRouter>,
  )

describe('RequestButton', () => {
  beforeEach(() => {
    sent.length = 0
    refusal = undefined
    status = { configured: true, reachable: true }
  })

  it('requests, then Requested takes it back after asking', async () => {
    renderButton({ title: film })

    fireEvent.click(await screen.findByRole('button', { name: 'Request' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Requested' }))
    expect(screen.getByRole('alertdialog').textContent).toContain('Unrequest The Matrix?')
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Request' })).toBeTruthy())
    expect(sent).toEqual([{ request: ['movie', 603, undefined] }, { unrequest: ['movie', 603, undefined] }])
  })

  it('takes back just the season it asked for', async () => {
    renderButton({ title: { ...film, mediaType: 'tv', tmdbId: 1399, availability: 'Requested' }, seasons: [2], name: 'Season 2', label: 'Request Season 2' })

    fireEvent.click(await screen.findByRole('button', { name: 'Unrequest Season 2' }))
    expect(screen.getByRole('alertdialog').textContent).toContain('Unrequest Season 2?')
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(sent).toEqual([{ unrequest: ['tv', 1399, [2]] }]))
  })

  it('says Can’t request, with why, when the request is refused', async () => {
    refusal = new ApiError(409, 'Conflict', ['Sonarr can’t add this show: TheTVDB doesn’t list it.'])
    renderButton({ title: film, size: 'large' })

    fireEvent.click(await screen.findByRole('button', { name: 'Request' }))

    expect(await screen.findByText('Can’t request')).toBeTruthy()
    expect(screen.getByRole('alert').textContent).toContain('TheTVDB')
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull()
  })

  it('greys out while Seerr isn’t answering', async () => {
    status = { configured: true, reachable: false }
    renderButton({ title: film, size: 'large' })

    expect(await screen.findByText('Seerr isn’t answering.')).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Request' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
