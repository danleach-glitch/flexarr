import { describe, expect, it } from 'vitest'
import { belongsToFocused } from './focusedKeys'

const press = (key: string, target: HTMLElement) => {
  const event = new KeyboardEvent('keydown', { key })
  Object.defineProperty(event, 'target', { value: target })
  return belongsToFocused(event)
}

describe('belongsToFocused', () => {
  it('leaves Space and Enter to buttons and menu items, and everything to fields', () => {
    const button = document.createElement('button')
    const item = Object.assign(document.createElement('div'), { role: 'menuitemradio' })
    item.setAttribute('role', 'menuitemradio')
    const field = document.createElement('input')

    expect(press(' ', button)).toBe(true)
    expect(press('Enter', item)).toBe(true)
    expect(press('k', field)).toBe(true)
  })

  it('leaves the page its shortcuts elsewhere, and on buttons for keys they don\'t use', () => {
    const button = document.createElement('button')

    expect(press(' ', document.body)).toBe(false)
    expect(press('ArrowLeft', button)).toBe(false)
    expect(press('f', button)).toBe(false)
  })

  it('leaves arrows to a menu', () => {
    const menu = document.createElement('div')
    menu.setAttribute('role', 'menu')
    const item = document.createElement('button')
    menu.append(item)

    expect(press('ArrowDown', item)).toBe(true)
  })
})
