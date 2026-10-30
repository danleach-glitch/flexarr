/** Says something briefly at the bottom of the window (an action from a menu that didn't work); see NoticeHost. */
export function showNotice(message: string) {
  window.dispatchEvent(new CustomEvent<string>('flexarr:notice', { detail: message }))
}
