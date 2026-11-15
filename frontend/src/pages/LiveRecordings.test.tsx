import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { clearApiCache } from '../api/client'
import LiveRecordings from './LiveRecordings'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

const now = Date.now()
const recording = (id: string, state: string, title: string) => ({
  id,
  state,
  title,
  subtitle: null,
  season: null,
  episode: null,
  channelName: 'BBC One',
  start: new Date(now - 60_000).toISOString(),
  end: new Date(now + 3_600_000).toISOString(),
  clashes: false,
  itemId: null,
  size: null,
  reason: null,
})

function openPage() {
  const deleted: string[] = []
  mockApi({
    '/livetv/recordings': {
      folder: null,
      freeBytes: null,
      streams: 1,
      upcoming: [recording('now', 'recording', 'The News'), recording('later', 'scheduled', 'The Film')],
      series: [{ id: 'series-1', title: 'Quiz Night', channelName: null, upcoming: 2, recorded: 1, newOnly: false, keepLatest: 0 }],
      recent: [],
    },
    'DELETE /livetv/recordings/now': () => void deleted.push('now'),
    'DELETE /livetv/recordings/later': () => void deleted.push('later'),
    'DELETE /livetv/recordings/series/series-1': () => void deleted.push('series-1'),
  })
  render(
    <MemoryRouter>
      <LiveRecordings />
    </MemoryRouter>,
  )
  return deleted
}

describe('stopping recordings', () => {
  it('asks before stopping one that is recording now, and stops it only when told', async () => {
    const deleted = openPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Stop recording' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText('Stop recording The News?')).toBeTruthy()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(deleted).toEqual([])

    fireEvent.click(screen.getByRole('button', { name: 'Stop recording' }))
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Stop recording' }))
    await waitFor(() => expect(deleted).toEqual(['now']))
  })

  it('takes a scheduled one off the list without asking', async () => {
    const deleted = openPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Don’t record' }))
    await waitFor(() => expect(deleted).toEqual(['later']))
    expect(screen.queryByRole('alertdialog')).toBeNull()
  })

  it('asks before stopping a series', async () => {
    const deleted = openPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Stop the series' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText('Stop recording Quiz Night?')).toBeTruthy()
    expect(deleted).toEqual([])
    fireEvent.click(within(dialog).getByRole('button', { name: 'Stop the series' }))
    await waitFor(() => expect(deleted).toEqual(['series-1']))
  })
})
