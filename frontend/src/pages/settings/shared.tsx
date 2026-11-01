import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { unlockFirst } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { ChevronLeft, ChevronRight, External } from '../../components/icons'
import { useSession } from '../../hooks/sessionContext'
import { appLink } from '../../lib/apps'

export function SaveBar({ saved, error, busy, label = 'Save changes' }: { saved?: string; error?: string; busy?: boolean; label?: string }) {
  return (
    <div className="form-actions">
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
      {saved && (
        <span className="muted" role="status">
          {saved}
        </span>
      )}
      <AsyncButton type="submit" className="button" busy={busy}>
        {label}
      </AsyncButton>
    </div>
  )
}

/** Opens the app in a new tab, at the address typed above it. */
export function OpenApp({ baseUrl, name, host }: { baseUrl: string; name: string; host?: string | null }) {
  const href = appLink(baseUrl, host || window.location.hostname)
  return (
    href && (
      <a className="open-app" href={href} target="_blank" rel="noreferrer" aria-label={`Open ${name}`} data-tip={`Open ${name}`}>
        <External />
      </a>
    )
  )
}

/** A page's title, a line on what it's for, an action beside the title, and a way back for a page reached from another. */
export function SettingsHeader({ title, back, action, children }: { title: string; back?: { to: string; label: string }; action?: ReactNode; children?: ReactNode }) {
  return (
    <header className="settings-header">
      {back && (
        <Link className="settings-back" to={back.to}>
          <ChevronLeft />
          {back.label}
        </Link>
      )}
      <div className="settings-title">
        <h2>{title}</h2>
        {action}
      </div>
      {children && <p className="settings-lede">{children}</p>}
    </header>
  )
}

/** A titled panel of settings rows, with a short note under it (whose they are, or what they do). */
export function SettingsGroup({ title, note, action, children }: { title?: string; note?: ReactNode; action?: ReactNode; children: ReactNode }) {
  return (
    <div className="settings-group">
      {(title || action) && (
        <div className="settings-group-head">
          {title && <h3 className="settings-subhead">{title}</h3>}
          {action}
        </div>
      )}
      <div className="panel setting-rows">{children}</div>
      {note && <p className="settings-note">{note}</p>}
    </div>
  )
}

/**
 * One setting: what it is (and a line of help) on the left, its control on the right. A label, so clicking the words
 * works the control; a div when the control is a button.
 */
export function SettingRow({ label, help, children, as = 'label' }: { label: ReactNode; help?: ReactNode; children?: ReactNode; as?: 'label' | 'div' }) {
  const Row = as
  return (
    <Row className={as === 'div' ? 'setting-row static' : 'setting-row'}>
      <span className="setting-text">
        <strong>{label}</strong>
        {help && <span className="muted">{help}</span>}
      </span>
      {children}
    </Row>
  )
}

/** An on/off setting, drawn as a switch. */
export function ToggleRow({ label, help, checked, disabled, onChange }: { label: ReactNode; help?: ReactNode; checked: boolean; disabled?: boolean; onChange: (on: boolean) => void }) {
  return (
    <SettingRow label={label} help={help}>
      <input type="checkbox" role="switch" className="switch" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
    </SettingRow>
  )
}

/** "Saved", or why it didn't, for settings that save as they change. */
export function SaveStatus({ saved, error }: { saved?: string | null; error?: string | null }) {
  if (error)
    return (
      <p className="error settings-status" role="alert">
        {error}
      </p>
    )
  return (
    <p className="muted settings-status" role="status">
      {saved}
    </p>
  )
}

/** A row that opens another page, with how things stand there now. */
export function SettingLink({ to, label, help }: { to: string; label: ReactNode; help?: ReactNode }) {
  return (
    <Link className="setting-row setting-link" to={to}>
      <span className="setting-text">
        <strong>{label}</strong>
        {help && <span className="muted">{help}</span>}
      </span>
      <ChevronRight />
    </Link>
  )
}

/**
 * Settings only the admin may see, on a page every profile has: shown when settings are unlocked here, and for the
 * main profile with them locked, a row that asks for the PIN.
 */
export function AdminOnly({ title, children }: { title: string; children: ReactNode }) {
  const { me, reloadMe } = useSession()
  if (me?.isAdmin === true) return <>{children}</>
  if (me?.canUnlock !== true) return null
  return (
    <SettingsGroup title={title}>
      <SettingRow as="div" label="Locked" help="Give the admin PIN to see and change these.">
        <button type="button" className="button quiet" onClick={() => void unlockFirst().then((unlocked) => unlocked && reloadMe?.())}>
          Unlock
        </button>
      </SettingRow>
    </SettingsGroup>
  )
}
