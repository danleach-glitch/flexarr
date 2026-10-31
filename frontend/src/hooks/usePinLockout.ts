import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'

/** How long a lockout lasts: what the server said, or else read from its message ("Try again in 29 seconds"). */
export function lockoutSeconds(error: ApiError): number | undefined {
  if (error.retryAfterSeconds !== undefined) return error.retryAfterSeconds
  const message = error.messages[0] ?? ''
  const count = /in (\d+) (second|minute)/.exec(message)
  if (count) return Number(count[1]) * (count[2] === 'minute' ? 60 : 1)
  return /in a minute/.test(message) ? 60 : undefined
}

/** "29 seconds", "a minute", "3 minutes", as the server words it. */
export function waitFor(seconds: number) {
  const minutes = Math.ceil(seconds / 60)
  return seconds < 60 ? `${seconds} second${seconds === 1 ? '' : 's'}` : minutes <= 1 ? 'a minute' : `${minutes} minutes`
}

/**
 * Too many wrong PINs: the wait counts down each second, and whatever asks for the PIN stays off until it's over.
 * `lockFrom` starts it from a refused attempt, saying whether it was a lockout; `onOver` runs when the wait ends.
 */
export function usePinLockout(onOver?: () => void) {
  /** When the lockout ends (ms since the epoch), while one is on. */
  const [lockedUntil, setLockedUntil] = useState<number>()
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (lockedUntil === undefined) return
    const timer = setInterval(() => {
      const at = Date.now()
      setNow(at)
      if (at >= lockedUntil) {
        setLockedUntil(undefined)
        onOver?.()
      }
    }, 1000)
    return () => clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- onOver is read when the wait ends
  }, [lockedUntil])

  const remaining = lockedUntil === undefined ? 0 : Math.max(0, Math.ceil((lockedUntil - now) / 1000))
  return {
    remaining,
    locked: remaining > 0,
    /** "Too many wrong PINs. Try again in 29 seconds.", counting down, while locked. */
    message: remaining > 0 ? `Too many wrong PINs. Try again in ${waitFor(remaining)}.` : undefined,
    lockFrom(error: unknown) {
      const seconds = error instanceof ApiError && error.status === 429 ? lockoutSeconds(error) : undefined
      if (seconds === undefined) return false
      setNow(Date.now())
      setLockedUntil(Date.now() + seconds * 1000)
      return true
    },
    reset: () => setLockedUntil(undefined),
  }
}
