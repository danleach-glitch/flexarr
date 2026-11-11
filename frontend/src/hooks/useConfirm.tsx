import { useCallback, useRef, useState, type ReactNode } from 'react'
import { ConfirmDialog } from '../components/ConfirmDialog'

export interface ConfirmQuestion {
  /** The question, as the dialog's heading. */
  title: string
  detail?: ReactNode
  /** What the final button does, named ("Remove profile"), not OK. */
  confirmLabel: string
  /** Red, for something that can't be taken back. */
  danger?: boolean
}

/**
 * The app's own confirm(), in place of the browser's: `ask` opens a ConfirmDialog and settles true when the named
 * action is chosen, false when it's cancelled. Render `dialog` somewhere in the component.
 */
export function useConfirm(): [(question: ConfirmQuestion) => Promise<boolean>, ReactNode] {
  const [question, setQuestion] = useState<ConfirmQuestion>()
  const settle = useRef<(answer: boolean) => void>(undefined)

  const ask = useCallback((next: ConfirmQuestion) => {
    // A question still open is answered No.
    settle.current?.(false)
    setQuestion(next)
    return new Promise<boolean>((resolve) => {
      settle.current = resolve
    })
  }, [])

  const answer = (yes: boolean) => {
    const resolve = settle.current
    settle.current = undefined
    setQuestion(undefined)
    resolve?.(yes)
  }

  const dialog = question ? (
    <ConfirmDialog title={question.title} confirmLabel={question.confirmLabel} danger={question.danger} onConfirm={() => answer(true)} onClose={() => answer(false)}>
      {question.detail}
    </ConfirmDialog>
  ) : null
  return [ask, dialog]
}
