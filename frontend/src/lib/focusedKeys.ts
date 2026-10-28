/**
 * Whether a key press belongs to the control that has focus rather than to the page's shortcuts: anything typed in a
 * field, Space or Enter on a button, link or menu item, and arrows inside a menu or list. The players use it so that
 * Space on the Subtitles button opens it instead of pausing.
 */
export function belongsToFocused(e: KeyboardEvent): boolean {
  const target = e.target
  if (!(target instanceof HTMLElement)) return false
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || target.isContentEditable) {
    return true
  }
  if (e.key.startsWith('Arrow') && target.closest('[role="menu"], [role="listbox"], [role="radiogroup"], [role="tablist"]')) return true
  const activates = e.key === ' ' || e.key === 'Enter'
  return activates && target.closest('button, a[href], [role="button"], [role^="menuitem"], [role="option"], [role="switch"], [role="tab"]') !== null
}
