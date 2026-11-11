import { useEffect, useState } from 'react'
import { Close } from './icons'
import { t } from '../i18n'

/** How long a notice stays: long enough to read, more for a longer one (a server's sentence), from 6 to 15 seconds. */
function noticeMs(message: string) {
  return Math.min(15_000, Math.max(6000, 2000 + message.length * 70))
}

/**
 * Where notices (`showNotice`) appear; one at a time, for a while that grows with their length. Pointing at one or
 * focusing its ✕ holds it there; ✕ closes it. In the layout, once.
 */
export function NoticeHost() {
  // `shown` counts each notice, so the same words twice start the time again.
  const [notice, setNotice] = useState<{ message: string; shown: number }>()
  const [held, setHeld] = useState(false)
  useEffect(() => {
    const show = (e: Event) => setNotice((n) => ({ message: (e as CustomEvent<string>).detail, shown: (n?.shown ?? 0) + 1 }))
    window.addEventListener('flexarr:notice', show)
    return () => window.removeEventListener('flexarr:notice', show)
  }, [])

  const message = notice?.message
  useEffect(() => {
    if (message === undefined || held) return
    const timer = setTimeout(() => setNotice(undefined), noticeMs(message))
    return () => clearTimeout(timer)
  }, [message, notice?.shown, held])

  const close = () => {
    setNotice(undefined)
    setHeld(false)
  }

  // The words are always there, so screen readers hear them when they arrive; out of sight while there are none. A
  // live region rather than role=status, which the pages' own status lines use.
  return (
    <div
      className={message ? 'notice' : 'visually-hidden'}
      onMouseEnter={() => setHeld(true)}
      onMouseLeave={() => setHeld(false)}
      onFocus={() => setHeld(true)}
      onBlur={() => setHeld(false)}
    >
      <span aria-live="polite" aria-atomic="true">
        {message}
      </span>
      {message && (
        <button type="button" className="icon-button notice-close" aria-label={t('Close')} onClick={close}>
          <Close />
        </button>
      )}
    </div>
  )
}
