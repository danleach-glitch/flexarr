/** "Ada Marsh" → "AM", "Plato" → "P": the first and last words' first letters. */
export function initialsOf(name: string) {
  const words = name.trim().split(/\s+/).filter((w) => /\p{L}/u.test(w))
  if (words.length === 0) return name.trim().charAt(0).toUpperCase()
  const first = Array.from(words[0])[0]
  const last = words.length > 1 ? Array.from(words.at(-1)!)[0] : ''
  return (first + last).toUpperCase()
}
