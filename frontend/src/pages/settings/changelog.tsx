/** CHANGELOG.md, in Settings › About and for a new release in Settings › Updates. */

export interface Release {
  title: string
  blocks: { kind: 'title' | 'text'; text: string; points: string[] }[]
}

/** CHANGELOG.md's shape: "## version · date", "### change", "- point" (with **bold**), and plain paragraphs. */
export function parse(markdown: string) {
  const releases: Release[] = []
  for (const line of markdown.split('\n')) {
    const release = releases.at(-1)
    if (line.startsWith('## ')) releases.push({ title: line.slice(3), blocks: [] })
    // Before the first release: the file's own title and introduction.
    else if (!release) continue
    else if (line.startsWith('### ')) release.blocks.push({ kind: 'title', text: line.slice(4), points: [] })
    else if (line.startsWith('- ') && release.blocks.at(-1)?.kind === 'title') release.blocks.at(-1)!.points.push(line.slice(2))
    else if (line.trim()) release.blocks.push({ kind: 'text', text: line, points: [] })
  }
  return releases
}

const inline = (text: string) => text.split(/(\*\*[^*]+\*\*)/).map((part, i) => (part.startsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part))

/** One release: its version and date, then each change with its points. */
export function ReleaseNotes({ release }: { release: Release }) {
  return (
    <article className="panel changelog-release">
      <h4 className="changelog-version">{release.title}</h4>
      {release.blocks.map((b, i) =>
        b.kind === 'title' ? (
          <div key={i} className="changelog-entry">
            <h5>{b.text}</h5>
            <ul>
              {b.points.map((p) => (
                <li key={p}>{inline(p)}</li>
              ))}
            </ul>
          </div>
        ) : (
          <p key={i} className="muted">
            {inline(b.text)}
          </p>
        ),
      )}
    </article>
  )
}
