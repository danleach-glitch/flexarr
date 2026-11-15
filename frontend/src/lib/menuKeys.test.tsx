import { fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { focusMenu, moveInMenu, tablistKeys } from './menuKeys'

function Menu() {
  return (
    <div role="menu" data-testid="menu" onKeyDown={(e) => moveInMenu(e, e.currentTarget)}>
      <button role="menuitemradio" aria-checked={false}>Off</button>
      <button role="menuitemradio" aria-checked={true}>English</button>
      <button role="menuitemradio" aria-checked={false} disabled>
        French
      </button>
      <button role="menuitemradio" aria-checked={false}>German</button>
    </div>
  )
}

function Tabs() {
  const [chosen, setChosen] = useState(0)
  return (
    <div role="tablist" onKeyDown={tablistKeys}>
      {['Season 1', 'Season 2', 'Season 3'].map((name, i) => (
        <button key={name} role="tab" aria-selected={i === chosen} tabIndex={i === chosen ? 0 : -1} onClick={() => setChosen(i)}>
          {name}
        </button>
      ))}
    </div>
  )
}

describe('menu keys', () => {
  it('opens on the chosen item, and Up and Down go round the rest, past what can’t be chosen', () => {
    render(<Menu />)
    focusMenu(screen.getByTestId('menu'))
    expect(screen.getByRole('menuitemradio', { name: 'English' })).toHaveFocus()

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowDown' })
    expect(screen.getByRole('menuitemradio', { name: 'German' })).toHaveFocus()
    fireEvent.keyDown(document.activeElement!, { key: 'ArrowDown' })
    expect(screen.getByRole('menuitemradio', { name: 'Off' })).toHaveFocus()
    fireEvent.keyDown(document.activeElement!, { key: 'ArrowUp' })
    expect(screen.getByRole('menuitemradio', { name: 'German' })).toHaveFocus()
    fireEvent.keyDown(document.activeElement!, { key: 'Home' })
    expect(screen.getByRole('menuitemradio', { name: 'Off' })).toHaveFocus()
  })

  it('moves along a row of tabs with Left and Right, choosing each, with only the chosen one a Tab stop', () => {
    render(<Tabs />)
    const first = screen.getByRole('tab', { name: 'Season 1' })
    first.focus()
    fireEvent.keyDown(first, { key: 'ArrowRight' })
    const second = screen.getByRole('tab', { name: 'Season 2' })
    expect(second).toHaveFocus()
    expect(second).toHaveAttribute('aria-selected', 'true')
    expect(second).toHaveAttribute('tabindex', '0')
    expect(first).toHaveAttribute('tabindex', '-1')
    fireEvent.keyDown(second, { key: 'ArrowLeft' })
    fireEvent.keyDown(document.activeElement!, { key: 'ArrowLeft' })
    expect(screen.getByRole('tab', { name: 'Season 3' })).toHaveFocus()
  })
})
