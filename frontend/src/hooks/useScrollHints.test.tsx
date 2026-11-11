import { fireEvent, render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { useScrollHints } from './useScrollHints'

/** A row 300 px wide holding 600 px of views, each 100 px. */
function Row({ chosen }: { chosen: number }) {
  const row = useScrollHints<HTMLElement>(chosen)
  return (
    <nav ref={row}>
      {[0, 1, 2, 3, 4, 5].map((i) => (
        <a key={i} aria-current={i === chosen ? 'page' : undefined}>
          {i}
        </a>
      ))}
    </nav>
  )
}

function sized() {
  const proto = HTMLElement.prototype
  const nav = (el: HTMLElement) => el.tagName === 'NAV'
  Object.defineProperty(proto, 'scrollWidth', { configurable: true, get() { return nav(this) ? 600 : 100 } })
  Object.defineProperty(proto, 'clientWidth', { configurable: true, get() { return nav(this) ? 300 : 100 } })
  proto.getBoundingClientRect = function (this: HTMLElement) {
    const scroller = this.parentElement
    const index = scroller ? [...scroller.children].indexOf(this) : 0
    const left = nav(this) ? 0 : index * 100 - (scroller?.scrollLeft ?? 0)
    return { left, width: nav(this) ? 300 : 100, top: 0, height: 30, right: left + 100, bottom: 30, x: left, y: 0, toJSON() {} } as DOMRect
  }
}

describe('a row of views that scrolls', () => {
  it('fades the edges with more beyond them, and scrolls the open view into the middle', () => {
    sized()
    const { container, rerender } = render(<Row chosen={0} />)
    const row = container.querySelector('nav')!
    expect(row.scrollLeft).toBe(0)
    expect(row).toHaveAttribute('data-more-end')
    expect(row).not.toHaveAttribute('data-more-start')

    rerender(<Row chosen={5} />)
    // The last view: as far as the row goes (it can't come to the middle).
    expect(row.scrollLeft).toBe(300)
    fireEvent.scroll(row)
    expect(row).toHaveAttribute('data-more-start')
    expect(row).not.toHaveAttribute('data-more-end')

    rerender(<Row chosen={3} />)
    // The fourth view's middle (350) at the row's middle (150).
    expect(row.scrollLeft).toBe(200)
  })
})
