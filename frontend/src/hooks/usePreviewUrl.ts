import { useEffect, useMemo } from 'react'

/** An object URL for previewing a chosen file, released when it changes. */
export function usePreviewUrl(file: File | null | undefined) {
  const url = useMemo(() => (file ? URL.createObjectURL(file) : undefined), [file])
  useEffect(
    () => () => {
      if (url) URL.revokeObjectURL(url)
    },
    [url],
  )
  return url
}
