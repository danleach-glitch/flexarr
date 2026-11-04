/** The list with `name` added, unless it's empty or already there (in any case). */
export const withName = (list: string[], name: string) => (name.trim() && !list.some((n) => n.toLowerCase() === name.trim().toLowerCase()) ? [...list, name.trim()] : list)

/** The sort title the server works out from a title: lower case, without a leading "the", "a" or "an". */
export function automaticSort(title: string) {
  const lower = title.trim().toLowerCase()
  const article = ['the ', 'a ', 'an '].find((a) => lower.startsWith(a) && lower.length > a.length)
  return article ? lower.slice(article.length) : lower
}

