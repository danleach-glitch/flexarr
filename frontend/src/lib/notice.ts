import { UnauthorizedError } from '../api/client'
import { errorText } from './errorText'

/** Says something briefly at the bottom of the window (an action from a menu that didn't work); see NoticeHost. */
export function showNotice(message: string) {
  window.dispatchEvent(new CustomEvent<string>('flexarr:notice', { detail: message }))
}

/** Says that an action didn't work, and why when it's known; nothing for one that was called off, or for a sign-in that's gone (the app asks again). */
export function noticeFailure(e: unknown) {
  if (e instanceof DOMException && e.name === 'AbortError') return
  if (e instanceof UnauthorizedError) return
  showNotice(errorText(e))
}
