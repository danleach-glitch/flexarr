/** Placeholder artwork until metadata agents bring real posters: a stable gradient per title. */
export function Artwork({ title, shape = 'poster' }: { title: string; shape?: 'poster' | 'wide' }) {
  let hash = 0
  for (const char of title) hash = (hash * 31 + char.charCodeAt(0)) | 0
  const hue = Math.abs(hash) % 360
  const initials = title
    .replace(/^(the|a|an)\s+/i, '')
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0])
    .join('')
    .toUpperCase()

  return (
    <div
      className={`artwork artwork-${shape}`}
      style={{ background: `linear-gradient(145deg, hsl(${hue} 45% 32%), hsl(${(hue + 40) % 360} 50% 16%))` }}
      aria-hidden="true"
    >
      <span>{initials}</span>
    </div>
  )
}
