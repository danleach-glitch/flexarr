import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, getSignIns, renameSignIn, revokeSignIn, signOut, signOutOthers, type SignIn } from '../api/client'
import { useApi } from '../hooks/useApi'
import { relativeTime } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { ConfirmDialog } from './ConfirmDialog'
import { Monitor, Tv } from './icons'

const threeDays = 3 * 24 * 60 * 60 * 1000

const where = (s: SignIn) => (s.zone === 'Local' ? 'Home network' : s.zone === 'Tailnet' ? 'Tailscale' : 'Internet')

/** "24 Oct", with the year when it isn't this one. */
const day = (iso: string) => {
  const date = new Date(iso)
  return date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: date.getFullYear() === new Date().getFullYear() ? undefined : 'numeric' })
}

const signedIn = (s: SignIn) => (s.kind === 'Device' ? 'connected' : 'signed in')

/**
 * One short line: who, where and when. An address is only worth showing from the internet (anything else is a device
 * in the house); the rest of the detail is on hover.
 */
const summary = (s: SignIn) =>
  [s.profileName, s.zone === 'Internet' && s.address ? `Internet (${s.address})` : where(s), `last used ${relativeTime(s.lastUsedAt)}`].filter(Boolean).join(' · ')

const detail = (s: SignIn, from = s.createdAt) =>
  `${s.kind === 'Device' ? 'Connected' : 'Signed in'} ${day(from) === day(s.createdAt) ? day(s.createdAt) : `between ${day(from)} and ${day(s.createdAt)}`}${s.address ? ` from ${s.address}` : ''}`

const KindIcon = ({ signIn }: { signIn: SignIn }) => <span className="sign-in-icon">{signIn.kind === 'Device' ? <Tv /> : <Monitor />}</span>

/** What tells one sign-in from another for a screen reader: "Chrome on Mac, signed in 24 Oct". */
const describe = (s: SignIn) => `${s.name}, ${s.isThisOne ? 'this browser' : `${signedIn(s)} ${day(s.createdAt)}`}`

/** Sign-ins that look the same in the list (the same browser signing in again and again) are shown as one. */
const sameKind = (s: SignIn) => (s.isThisOne ? s.id : [s.kind, s.name, s.zone, s.address ?? '', s.profileId].join('\u0000'))

/** Signed in during the last three days, and since this browser did: worth a second look if nobody remembers doing it. */
const newer = (s: SignIn, thisOne: SignIn | undefined) =>
  !s.isThisOne && Date.now() - new Date(s.createdAt).getTime() < threeDays && (!thisOne || new Date(s.createdAt) > new Date(thisOne.createdAt))

type Asking = { one: SignIn } | { group: SignIn[] } | { others: number }

/**
 * Browsers and TVs signed in with their own token (Settings › Access). Signing one out ends just that one: a lost TV
 * or a shared laptop, without changing the API key everywhere else.
 */
export function SignIns() {
  const list = useApi(getSignIns, [])
  const [asking, setAsking] = useState<Asking>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [done, setDone] = useState<string>()
  const [open, setOpen] = useState<Record<string, boolean>>({})
  const [editing, setEditing] = useState<string>()

  async function act(work: () => Promise<string | void>) {
    setBusy(true)
    setError(undefined)
    try {
      const said = await work()
      setAsking(undefined)
      setDone(said || undefined)
      list.reload()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      list.reload() // some of a group may have gone
    } finally {
      setBusy(false)
    }
  }

  function confirm() {
    if (!asking) return
    if ('one' in asking) {
      const s = asking.one
      if (s.isThisOne) {
        void act(async () => {
          await signOut()
          window.location.assign('/')
        })
      } else {
        void act(async () => {
          await revokeSignIn(s.id)
          return `Signed out ${s.name}.`
        })
      }
    } else if ('group' in asking) {
      const group = asking.group
      void act(async () => {
        for (const s of group) await revokeSignIn(s.id)
        return `Signed out ${group.length.toLocaleString()} sign-ins of ${group[0].name}.`
      })
    } else {
      void act(async () => {
        const { signedOut } = await signOutOthers()
        return `Signed out ${signedOut.toLocaleString()} ${signedOut === 1 ? 'browser or TV' : 'browsers and TVs'}.`
      })
    }
  }

  if (!list.data) return <p className="muted">{list.error ? list.error.message : 'Loading…'}</p>

  const thisOne = list.data.find((s) => s.isThisOne)
  const others = list.data.filter((s) => !s.isThisOne)
  const isNew = (s: SignIn) => newer(s, thisOne)

  const groups: SignIn[][] = []
  const byKind = new Map<string, SignIn[]>()
  for (const s of list.data) {
    const found = byKind.get(sameKind(s))
    if (found) found.push(s)
    else {
      const group = [s]
      byKind.set(sameKind(s), group)
      groups.push(group)
    }
  }

  const row = (s: SignIn) => (
    <li key={s.id}>
      {editing === s.id ? (
        <RenameForm
          signIn={s}
          onDone={(renamed) => {
            setEditing(undefined)
            if (renamed) list.reload()
            requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-rename="${s.id}"]`)?.focus())
          }}
        />
      ) : (
        <>
          <KindIcon signIn={s} />
          <span className="sign-in-text">
            <span className="sign-in-name">
              <strong>{s.name}</strong>
              {s.isThisOne && <span className="pill">This browser</span>}
              {isNew(s) && (
                <span className="pill new" title="Signed in recently. If you don’t recognise it, sign it out.">
                  New
                </span>
              )}
            </span>
            <span className="muted sign-in-meta" title={detail(s)}>
              {summary(s)}
            </span>
          </span>
        </>
      )}
      {editing !== s.id && (
        <span className="sign-in-actions">
          <button className="button quiet small" data-rename={s.id} aria-label={`Rename ${describe(s)}`} onClick={() => setEditing(s.id)}>
            Rename
          </button>
          <button className="button quiet small" aria-label={`Sign out ${describe(s)}`} disabled={busy} onClick={() => setAsking({ one: s })}>
            Sign out
          </button>
        </span>
      )}
    </li>
  )

  return (
    <div className="panel">
      {list.data.length === 0 ? (
        <p className="muted">Nothing has signed in with its own token yet. Browsers on your home network or Tailscale may not need to.</p>
      ) : (
        <ul className="plain-list sign-in-list">
          {groups.map((group) => {
            if (group.length === 1) return row(group[0])
            const [latest] = group
            const oldest = group.reduce((a, b) => (a.createdAt < b.createdAt ? a : b))
            const expanded = open[latest.id] ?? false
            return (
              <li key={latest.id} className="sign-in-group">
                <KindIcon signIn={latest} />
                <span className="sign-in-text">
                  <span className="sign-in-name">
                    <strong>{latest.name}</strong>
                    <span className="pill">{group.length.toLocaleString()} sign-ins</span>
                    {group.some(isNew) && <span className="pill new">New</span>}
                  </span>
                  <span className="muted sign-in-meta" title={detail(latest, oldest.createdAt)}>
                    {summary(latest)}
                  </span>
                </span>
                <span className="sign-in-actions">
                  <button className="button quiet small" aria-expanded={expanded} onClick={() => setOpen((o) => ({ ...o, [latest.id]: !expanded }))}>
                    {expanded ? 'Hide' : 'Show each'}
                  </button>
                  <button
                    className="button quiet small"
                    aria-label={`Sign out all ${group.length} sign-ins of ${latest.name}`}
                    disabled={busy}
                    onClick={() => setAsking({ group })}
                  >
                    Sign out all {group.length.toLocaleString()}
                  </button>
                </span>
                {expanded && <ul className="plain-list sign-in-list sign-in-each">{group.map(row)}</ul>}
              </li>
            )
          })}
        </ul>
      )}
      {done && (
        <p className="muted sign-in-done" role="status">
          {done}
        </p>
      )}
      {error && !asking && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {others.length > 1 && (
        <div className="sign-in-footer">
          <button className="button danger small" disabled={busy} onClick={() => setAsking({ others: others.length })}>
            Sign out every other browser and TV
          </button>
        </div>
      )}
      {asking && (
        <ConfirmDialog
          title={
            'one' in asking
              ? asking.one.isThisOne
                ? 'Sign out of this browser?'
                : `Sign out ${asking.one.name}?`
              : 'group' in asking
                ? `Sign out all ${asking.group.length.toLocaleString()} sign-ins of ${asking.group[0].name}?`
                : 'Sign out every other browser and TV?'
          }
          confirmLabel="Sign out"
          cancelLabel="Keep signed in"
          danger
          busy={busy}
          error={error}
          onConfirm={confirm}
          onClose={() => {
            setAsking(undefined)
            setError(undefined)
          }}
        >
          <p>
            {'one' in asking
              ? asking.one.isThisOne
                ? 'You’ll need the API key to sign in here again.'
                : asking.one.kind === 'Device'
                  ? `It will need to connect again. Connected ${day(asking.one.createdAt)}, last used ${relativeTime(asking.one.lastUsedAt)}.`
                  : `It will need the API key to sign in again. Signed in ${day(asking.one.createdAt)}, last used ${relativeTime(asking.one.lastUsedAt)}.`
              : 'group' in asking
                ? 'Each will need to sign in or connect again.'
                : `${asking.others.toLocaleString()} browsers and TVs will need to sign in or connect again. This browser stays signed in.`}
          </p>
        </ConfirmDialog>
      )}
    </div>
  )
}

/** In place of the row: a name people in the house recognise ("Kitchen iPad"). Escape puts it back as it was. */
function RenameForm({ signIn, onDone }: { signIn: SignIn; onDone: (renamed: boolean) => void }) {
  const [name, setName] = useState(signIn.name)
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => {
    input.current?.focus()
    input.current?.select()
  }, [])

  async function save(e: FormEvent) {
    e.preventDefault()
    if (!name.trim() || name.trim() === signIn.name) return onDone(false)
    setSaving(true)
    try {
      await renameSignIn(signIn.id, name.trim())
      onDone(true)
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  return (
    <form className="sign-in-rename" onSubmit={(e) => void save(e)} onKeyDown={(e) => e.key === 'Escape' && onDone(false)}>
      <label className="field">
        <span className="field-label">Name for {signIn.name}</span>
        <input ref={input} value={name} onChange={(e) => setName(e.target.value)} maxLength={80} autoComplete="off" />
      </label>
      <div className="form-actions">
        <button type="button" className="button quiet small" onClick={() => onDone(false)}>
          Cancel
        </button>
        <AsyncButton type="submit" className="button small" busy={saving}>
          Save
        </AsyncButton>
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </form>
  )
}
