import { useRef, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ChevronLeft, ChevronRight } from './icons'

/** A titled horizontal shelf. Arrow buttons page by a screenful; it also scrolls by trackpad and keyboard focus. */
export function Row({ title, link, style = 'poster', children }: { title: string; link?: string | null; style?: string; children: ReactNode }) {
  const track = useRef<HTMLDivElement>(null)
  const page = (direction: 1 | -1) => {
    const el = track.current
    if (el) el.scrollBy({ left: direction * el.clientWidth * 0.85, behavior: 'smooth' })
  }

  return (
    <section className={`row row-${style}`}>
      <header className="row-header">
        <h2>{link ? <Link to={link}>{title}</Link> : title}</h2>
        <div className="row-paging">
          <button className="icon-button" onClick={() => page(-1)} aria-label={`Scroll ${title} back`}>
            <ChevronLeft />
          </button>
          <button className="icon-button" onClick={() => page(1)} aria-label={`Scroll ${title} forward`}>
            <ChevronRight />
          </button>
        </div>
      </header>
      <div className="row-track" ref={track}>
        {children}
      </div>
    </section>
  )
}
