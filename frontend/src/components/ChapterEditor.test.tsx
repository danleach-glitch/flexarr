import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { ChapterEditor } from './ChapterEditor'

afterEach(() => vi.unstubAllGlobals())

const chapters = [
  { index: 0, title: 'One', startSeconds: 0, endSeconds: 60 },
  { index: 1, title: 'Two', startSeconds: 60, endSeconds: 120 },
  { index: 2, title: 'Three', startSeconds: 120, endSeconds: 200 },
]

describe('ChapterEditor', () => {
  it('shifts in the form, keeping renames, and Save sends both', async () => {
    const sent: unknown[] = []
    mockApi({
      'PUT /items/b1/chapters': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as { chapters: { title: string; startSeconds: number }[] }
        sent.push(body)
        return body.chapters.map((c, i) => ({ index: i, ...c, endSeconds: 0 }))
      },
    })
    const onSaved = vi.fn()
    render(<ChapterEditor bookId="b1" chapters={chapters} hasAsin={false} onSaved={onSaved} onClose={() => undefined} />)

    fireEvent.change(screen.getByLabelText('Chapter 2 title'), { target: { value: 'The second' } })
    fireEvent.change(screen.getByPlaceholderText('seconds'), { target: { value: '2.5' } })
    fireEvent.click(screen.getByRole('button', { name: 'Shift' }))

    expect(screen.getByLabelText<HTMLInputElement>('Chapter 2 title').value).toBe('The second')
    expect(screen.getByLabelText<HTMLInputElement>('Chapter 1 start').value).toBe('0:00')
    expect(screen.getByLabelText<HTMLInputElement>('Chapter 2 start').value).toBe('1:02.5')
    expect(sent).toEqual([])

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([
      {
        chapters: [
          { title: 'One', startSeconds: 0 },
          { title: 'The second', startSeconds: 62.5 },
          { title: 'Three', startSeconds: 122.5 },
        ],
      },
    ])
  })

  it('marks a start two chapters share, and asks before Done throws edits away', () => {
    const onClose = vi.fn()
    render(<ChapterEditor bookId="b1" chapters={chapters} hasAsin={false} onSaved={() => undefined} onClose={onClose} />)

    fireEvent.click(screen.getByRole('button', { name: 'Add a chapter' }))
    expect(screen.getByLabelText('Chapter 4 start').getAttribute('aria-invalid')).toBe('true')
    expect(screen.getByLabelText('Chapter 3 start').getAttribute('aria-invalid')).toBe('true')
    expect(screen.getByText('Two chapters start at the same time.')).toBeTruthy()
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save' }).disabled).toBe(true)

    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(onClose).not.toHaveBeenCalled()
    expect(screen.getByRole('alert').textContent).toBe('Your changes aren’t saved.')
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }))
    expect(onClose).toHaveBeenCalled()
  })
})
