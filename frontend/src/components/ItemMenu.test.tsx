import { fireEvent, render, screen } from '@testing-library/react'
import { useRef, useState } from 'react'
import { describe, expect, it } from 'vitest'
import { Menu, type MenuPoint } from './ItemMenu'

/** The film page's ⋯ and its Version menu, as ItemPage has them. */
function MoreAndVersion() {
  const button = useRef<HTMLButtonElement>(null)
  const [moreAt, setMoreAt] = useState<MenuPoint | null>(null)
  const [versionAt, setVersionAt] = useState<MenuPoint | null>(null)
  return (
    <>
      <button ref={button} onClick={() => setMoreAt({ x: 0, y: 0 })}>
        More
      </button>
      {moreAt && <Menu label="More" at={moreAt} actions={[{ label: 'Version: Automatic', run: () => setVersionAt(moreAt) }]} onClose={() => setMoreAt(null)} focusBack={button} />}
      {versionAt && (
        <Menu
          label="Version"
          at={versionAt}
          actions={[
            { label: 'Automatic', checked: false, run: () => undefined },
            { label: '4K', checked: true, run: () => undefined },
          ]}
          onClose={() => setVersionAt(null)}
          focusBack={button}
        />
      )}
    </>
  )
}

describe('a menu handing over to another', () => {
  it('leaves focus on the ticked row of the new menu, and gives it back to ⋯ when that closes', () => {
    render(<MoreAndVersion />)
    fireEvent.click(screen.getByRole('button', { name: 'More' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Version: Automatic' }))

    const ticked = screen.getByRole('menuitemradio', { name: '4K' })
    expect(ticked).toHaveFocus()
    fireEvent.keyDown(ticked, { key: 'ArrowUp' })
    expect(screen.getByRole('menuitemradio', { name: 'Automatic' })).toHaveFocus()

    fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
    expect(screen.queryByRole('menu')).toBeNull()
    expect(screen.getByRole('button', { name: 'More' })).toHaveFocus()
  })
})
