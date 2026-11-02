import type { MusicExternal } from '../api/client'

/** Where an outside artist or album opens: its library page if you have it, else its page here, else nowhere. */
export function musicLink(item: MusicExternal): string | undefined {
  if (item.libraryItemId) return `/item/${item.libraryItemId}`
  if (item.deezerId > 0) return `/discover/music/${item.kind}/${item.deezerId}`
  return undefined
}
