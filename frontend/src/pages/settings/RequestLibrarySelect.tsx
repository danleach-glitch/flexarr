import type { RequestDestination } from '../../api/client'

/**
 * Which library a kind of request goes to. Seerr's default library stands for "not chosen" (null), so a profile that
 * never chose follows Seerr if its default changes; a choice that's since been hidden or gone shows as the default too.
 */
export function RequestLibrarySelect({
  choices,
  value,
  onChange,
  label,
}: {
  choices: RequestDestination[]
  value: string | null | undefined
  onChange: (libraryId: string | null) => void
  label?: string
}) {
  const fallback = choices.find((d) => d.isDefault)
  const shown = choices.some((d) => d.id === value) ? value! : (fallback?.id ?? '')
  return (
    <select aria-label={label} value={shown} onChange={(e) => onChange(e.target.value === '' || e.target.value === fallback?.id ? null : e.target.value)}>
      {!fallback && <option value="">Wherever Seerr adds them</option>}
      {choices.map((d) => (
        <option key={d.id} value={d.id}>
          {d.name}
        </option>
      ))}
    </select>
  )
}
