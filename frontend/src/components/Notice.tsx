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
  return (
    <div className="notice" role="status" aria-live="polite" hidden={!message}>
      {message}
    </div>
  )
}
