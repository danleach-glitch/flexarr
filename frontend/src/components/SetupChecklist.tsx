import { useState } from 'react'
import { Link } from 'react-router'
import { saveSetupGuide, type SetupGuide } from '../api/client'
import { isDone, setupItems as items } from '../lib/setupGuide'

/** What's left of the setup guide after leaving it part way, each step a way back into it. */
export function SetupChecklist({ guide }: { guide: SetupGuide }) {
  const [hidden, setHidden] = useState(false)
  if (hidden) return null
  const done = items.filter((i) => isDone(guide, i.id)).length
  const next = items.find((i) => !isDone(guide, i.id))!

  function hide() {
    setHidden(true)
    void saveSetupGuide({ dismissed: true }).catch(() => undefined)
  }

  return (
    <section className="setup-checklist" aria-labelledby="setup-checklist-title">
      <div className="setup-checklist-head">
        <h2 id="setup-checklist-title">Finish setting up</h2>
        <span className="muted">
          {done} of {items.length}
        </span>
      </div>
      <ul>
        {items.map((i) => (
          <li key={i.id} className={isDone(guide, i.id) ? 'done' : undefined}>
            <Link to={`/setup?step=${i.id}`}>{i.label}</Link>
          </li>
        ))}
      </ul>
      <div className="welcome-actions">
        <Link className="button" to={`/setup?step=${next.id}`}>
          Continue
        </Link>
        <button type="button" className="button quiet" onClick={hide}>
          Hide
        </button>
      </div>
    </section>
  )
}
