import { useEffect, useState } from 'react'

/** Where notices (`showNotice`) appear; one at a time, for a few seconds. In the layout, once. */
export function NoticeHost() {
  const [message, setMessage] = useState<string>()
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    const show = (e: Event) => {
      setMessage((e as CustomEvent<string>).detail)
      clearTimeout(timer)
      timer = setTimeout(() => setMessage(undefined), 4000)
    }
    window.addEventListener('flexarr:notice', show)
    return () => {
      clearTimeout(timer)
      window.removeEventListener('flexarr:notice', show)
    }
  }, [])
  // Always there, so screen readers hear the text when it arrives; out of sight while it's empty. A live region
  // rather than role=status, which the pages' own status lines use.
  return (
    <div className={message ? 'notice' : 'visually-hidden'} aria-live="polite" aria-atomic="true">
      {message}
    </div>
  )
}
