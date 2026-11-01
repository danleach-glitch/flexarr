/** Only a picked file's object URL or one of the server's own image paths ever goes into `src`. */
export function safeImageSrc(src: string): string | undefined {
  if (src.startsWith('blob:')) return src
  if (src.startsWith('/') && !src.startsWith('//')) return src
  return undefined
}
