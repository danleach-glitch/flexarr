import { useEffect, useState } from 'react'
import { t } from '../i18n'

/**
 * Copies `text`, and says so for a moment. Over plain http on the home network browsers have no clipboard API, so it
 * falls back to the older copy command.
 */
export function CopyButton({ text, label, what }: { text: string; label?: string; what?: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  useEffect(() => {
    if (state === 'idle') return
    const timer = setTimeout(() => setState('idle'), 2000)
    return () => clearTimeout(timer)
  }, [state])

  async function copy() {
    try {
      if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text)
      else copyTheOldWay(text)
      setState('copied')
    } catch {
      setState('failed')
    }
  }

  return (
    <button type="button" className="button quiet small" aria-label={what ? t('Copy {what}', { what }) : undefined} onClick={() => void copy()}>
      <span aria-live="polite">{state === 'copied' ? t('Copied') : state === 'failed' ? t('Couldn’t copy') : (label ?? t('Copy'))}</span>
    </button>
  )
}

function copyTheOldWay(text: string) {
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.opacity = '0'
  const focused = document.activeElement instanceof HTMLElement ? document.activeElement : null
  document.body.append(area)
  area.select()
  const ok = document.execCommand('copy')
  area.remove()
  focused?.focus()
  if (!ok) throw new Error('Copy failed')
}
