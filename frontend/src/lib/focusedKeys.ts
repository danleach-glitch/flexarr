const SLIDER_KEYS = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown', 'Tab'])
const PRESSABLE = 'button, a[href], [role="button"], [role^="menuitem"], [role="option"], [role="switch"], [role="tab"]'

/** The control last clicked or tapped, which keeps focus afterwards. Moving focus with Tab forgets it. */
let clicked: Element | null = null
if (typeof document !== 'undefined') {
  document.addEventListener('pointerdown', (e) => {
    clicked = e.target instanceof Element ? e.target.closest(PRESSABLE) : null
  }, true)
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Tab') clicked = null
  }, true)
}

/**
 * Whether a key press belongs to the control that has focus rather than to the page's shortcuts: anything typed in a
 * field, Space or Enter on a button, link or menu item, and arrows inside a menu or list. The players use it so that
 * Space on the Subtitles button opens it instead of pausing. A button that has focus only because it was clicked (Full
 * screen, say) doesn't take Space: that stays play/pause, as people expect after using the mouse.
 */
export function belongsToFocused(e: KeyboardEvent): boolean {
  const target = e.target
  if (!(target instanceof HTMLElement)) return false
  // A slider (the seek bar, the volume) uses only the keys that move it: Space and the shortcuts stay the page's.
  if (target instanceof HTMLInputElement && target.type === 'range') return SLIDER_KEYS.has(e.key)
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || target.isContentEditable) {
    return true
  }
  if (e.key.startsWith('Arrow') && target.closest('[role="menu"], [role="listbox"], [role="radiogroup"], [role="tablist"]')) return true
  const activates = e.key === ' ' || e.key === 'Enter'
  const pressable = target.closest(PRESSABLE)
  if (e.key === ' ' && pressable !== null && pressable === clicked) return false
  return activates && pressable !== null
}
