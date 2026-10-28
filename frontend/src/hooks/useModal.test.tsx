import { fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { useModal } from './useModal'

function Dialog({ onClose }: { onClose: () => void }) {
  const dialog = useModal<HTMLDivElement>(onClose)
  return (
    <div ref={dialog} role="dialog" aria-modal="true">
      <button>First</button>
      <button>Last</button>
    </div>
  )
}

function Page() {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button onClick={() => setOpen(true)}>Open</button>
      {open && <Dialog onClose={() => setOpen(false)} />}
    </>
  )
}

describe('useModal', () => {
  it('starts inside, keeps Tab inside, and gives the focus back on Escape', () => {
    render(<Page />)
    const opener = screen.getByRole('button', { name: 'Open' })
    opener.focus()
    fireEvent.click(opener)

    expect(screen.getByRole('button', { name: 'First' })).toHaveFocus()
    screen.getByRole('button', { name: 'Last' }).focus()
    fireEvent.keyDown(document.activeElement!, { key: 'Tab' })
    expect(screen.getByRole('button', { name: 'First' })).toHaveFocus()
    fireEvent.keyDown(document.activeElement!, { key: 'Tab', shiftKey: true })
    expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus()

    fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
  })
})
