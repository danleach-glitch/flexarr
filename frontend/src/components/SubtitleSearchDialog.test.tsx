import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { SubtitleSearchDialog } from './SubtitleSearchDialog'

afterEach(() => vi.unstubAllGlobals())

const result = (id: string, release: string, matchesFile = false, hearingImpaired = false) => ({
  id,
  provider: 'opensubtitlescom',
  language: 'en',
  languageLabel: hearingImpaired ? 'English (SDH)' : 'English',
  release,
  hearingImpaired,
  forced: false,
  score: matchesFile ? 100 : 91,
  matchesFile,
  uploader: null,
})

describe('Subtitle search', () => {
  it('lists what Bazarr found, marks the ones made for the file, and downloads the one chosen', async () => {
    const sent: unknown[] = []
    mockApi({
      '/items/film-1/subtitles/search': { results: [result('r1', 'Film.2020.1080p-GRP', true, true), result('r2', 'Film 2020 WEB')] },
      'POST /items/film-1/subtitles/download': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)))
        return { key: 'x0', label: 'English' }
      },
    })
    const downloaded = vi.fn()
    render(<SubtitleSearchDialog itemId="film-1" fileId="file-1" title="Film" onDownloaded={downloaded} onClose={() => undefined} />)

    const match = await screen.findByRole('option', { name: /Film\.2020\.1080p-GRP/ })
    expect(match).toHaveTextContent('Matches your file')
    expect(match).toHaveTextContent('English (SDH)')
    expect(screen.getByRole('option', { name: /Film 2020 WEB/ })).not.toHaveTextContent('Matches your file')

    fireEvent.click(screen.getByRole('option', { name: /Film 2020 WEB/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Download' }))

    await vi.waitFor(() => expect(downloaded).toHaveBeenCalledWith({ key: 'x0', label: 'English' }))
    expect(sent).toEqual([{ result: 'r2', fileId: 'file-1' }])
  })

  it('says why when Bazarr can’t search', async () => {
    mockApi({
      '/items/film-1/subtitles/search': new Response(JSON.stringify({ detail: 'Bazarr can’t be reached: refused' }), { status: 502 }),
    })
    render(<SubtitleSearchDialog itemId="film-1" title="Film" onDownloaded={() => undefined} onClose={() => undefined} />)

    expect(await screen.findByText('Bazarr can’t be reached: refused')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pick the best for me' })).toBeDisabled()
  })
})
