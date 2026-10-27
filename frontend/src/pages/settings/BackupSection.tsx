import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  backUpNow,
  deleteBackup,
  downloadBackup,
  exportSettings,
  forgetBackupFile,
  getBackups,
  getProfiles,
  importSettingsFile,
  openBackupFile,
  restoreBackup,
  restoreBackupFile,
  saveBackupSettings,
  uploadBackupFile,
  type Backup,
  type BackupContents,
  type BackupKind,
  type BackupUpload,
  type SettingsFile,
  type SettingsImportResult,
} from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { Download, Restore, Trash } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { formatSize, libraryTypeLabel } from '../../lib/format'
import { SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

const kindLabel: Record<BackupKind, string> = {
  Manual: 'Made by hand',
  Nightly: 'Nightly',
  PreUpgrade: 'Before an upgrade',
  PreRestore: 'Before a restore',
}

/** The parts of a settings file, in the order they're offered. Server keys and this machine's paths start unticked. */
const sections: { id: string; label: string; help: string; off?: boolean }[] = [
  { id: 'libraries', label: 'Libraries', help: 'Added if there’s none of that name here, then scanned.' },
  { id: 'connections', label: 'Connections', help: 'Sonarr, Radarr, Lidarr, Seerr and path mappings, with their keys.' },
  { id: 'liveTv', label: 'Live TV', help: 'The Dispatcharr source and the DVR’s settings.' },
  { id: 'streaming', label: 'Streaming', help: 'Hardware acceleration and limits.' },
  { id: 'layout', label: 'Home and Browse', help: 'Which rows show, in what order.' },
  { id: 'podcasts', label: 'Podcasts', help: 'The country for charts.' },
  { id: 'notifications', label: 'Notifications', help: 'Push notifications to phones.' },
  { id: 'access', label: 'Access', help: 'Who has to sign in, and trusted proxies. Asks for the main profile’s PIN.' },
  { id: 'machine', label: 'This machine’s paths', help: 'ffmpeg, the cache and backups folders. Usually wrong on another machine, and it asks for the main profile’s PIN.', off: true },
  {
    id: 'keys',
    label: 'Server keys',
    help: 'The API key and webhook secret. Apps and Sonarr or Radarr set up with the old server’s keep working; anything using this server’s current key needs the new one.',
    off: true,
  },
]

const sectionLabel = (id: string) => sections.find((s) => s.id === id)?.label ?? id

function when(iso: string) {
  const date = new Date(iso)
  const thisYear = date.getFullYear() === new Date().getFullYear()
  return date.toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: thisYear ? undefined : 'numeric', hour: '2-digit', minute: '2-digit' })
}

type Dialog =
  | { kind: 'download'; backup: Backup }
  | { kind: 'export' }
  | { kind: 'delete'; backup: Backup }
  | { kind: 'restore'; createdAt: string; name?: string; uploadId?: string }

export function BackupSection() {
  const backups = useApi(getBackups, [])
  const profiles = useApi(getProfiles, [])
  const mainHasPin = profiles.data?.find((p) => p.isMain)?.hasPin ?? false
  const [dialog, setDialog] = useState<Dialog>()
  const [error, setError] = useState<string>()
  const [restarting, setRestarting] = useState(false)

  if (restarting) return <Restarting />

  async function makeOne() {
    setError(undefined)
    try {
      await backUpNow()
      backups.reload()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <section>
      <SettingsHeader title="Backup and restore">
        A backup holds everything Flexarr can’t rebuild from your media: profiles, what everyone’s watched, playlists, collections, settings and sign-ins.
        Artwork and previews are left out; they come back by themselves.
      </SettingsHeader>

      <SettingsGroup
        title="Backups"
        action={
          <AsyncButton className="button small" onClick={makeOne}>
            Back up now
          </AsyncButton>
        }
        note={
          backups.data && (
            <>
              Kept in <code>{backups.data.folder}</code>.
            </>
          )
        }
      >
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {backups.data?.folderProblem && (
          <p className="error" role="alert">
            {backups.data.folderProblem}
          </p>
        )}
        {backups.loading && !backups.data && <p className="muted">Loading…</p>}
        {backups.error && (
          <p className="error" role="alert">
            Couldn’t list the backups: {backups.error.message}
          </p>
        )}
        {backups.data?.backups.length === 0 && <p className="muted">No backups yet.</p>}
        {backups.data?.backups.map((b) => (
          <div className="library-row backup-row" key={b.name}>
            <div>
              <h3>{when(b.createdAt)}</h3>
              <p className="muted">
                {kindLabel[b.kind]} · {formatSize(b.size)}
                {b.flexarrVersion && ` · Flexarr ${b.flexarrVersion}`}
              </p>
            </div>
            <div className="library-row-actions">
              <IconButton label="Download" icon={<Download />} onClick={() => setDialog({ kind: 'download', backup: b })} />
              <IconButton label="Restore" icon={<Restore />} onClick={() => setDialog({ kind: 'restore', createdAt: b.createdAt, name: b.name })} />
              <IconButton label="Delete" icon={<Trash />} className="button quiet small icon danger-icon" onClick={() => setDialog({ kind: 'delete', backup: b })} />
            </div>
          </div>
        ))}
      </SettingsGroup>

      {backups.data && <Automatic nightly={backups.data.nightly} keep={backups.data.keepNightly} folder={backups.data.folderSetting} folderInUse={backups.data.folder} onSaved={backups.reload} />}

      <SettingsGroup title="Settings file" note="Libraries and the server’s settings, without profiles or watch history: for setting up another server, or keeping a copy.">
        <SettingRow as="div" label="Export settings" help="Saves a file to this computer.">
          <button className="button quiet small" onClick={() => setDialog({ kind: 'export' })}>
            Export…
          </button>
        </SettingRow>
      </SettingsGroup>

      <FromFile mainHasPin={mainHasPin} onRestore={(upload, contents) => setDialog({ kind: 'restore', createdAt: contents.createdAt, uploadId: upload.id })} />

      {(dialog?.kind === 'download' || dialog?.kind === 'export') && (
        <PassphraseDialog
          title={dialog.kind === 'export' ? 'Export settings' : 'Download this backup'}
          onSave={(passphrase) => (dialog.kind === 'export' ? exportSettings(passphrase) : downloadBackup(dialog.backup.name, passphrase))}
          onClose={() => setDialog(undefined)}
        />
      )}
      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title="Delete this backup?"
          confirmLabel="Delete"
          danger
          onConfirm={async () => {
            await deleteBackup(dialog.backup.name)
            setDialog(undefined)
            backups.reload()
          }}
          onClose={() => setDialog(undefined)}
        >
          <p>
            {kindLabel[dialog.backup.kind]}, {when(dialog.backup.createdAt)}.
          </p>
        </ConfirmDialog>
      )}
      {dialog?.kind === 'restore' && (
        <RestoreDialog
          createdAt={dialog.createdAt}
          mainHasPin={mainHasPin}
          onRestore={(confirmation) => (dialog.uploadId ? restoreBackupFile(dialog.uploadId, confirmation) : restoreBackup(dialog.name!, confirmation))}
          onRestarting={() => {
            setDialog(undefined)
            setRestarting(true)
          }}
          onClose={() => setDialog(undefined)}
        />
      )}
    </section>
  )
}

/** Nightly backups, and where backups go. */
function Automatic({ nightly, keep, folder, folderInUse, onSaved }: { nightly: boolean; keep: number; folder: string | null; folderInUse: string; onSaved: () => void }) {
  const [path, setPath] = useState(folder ?? '')
  const [error, setError] = useState<string>()
  const [saved, setSaved] = useState<string>()

  async function save(update: { folder?: string; nightly?: boolean }) {
    setError(undefined)
    try {
      await saveBackupSettings(update)
      setSaved('Saved')
      setTimeout(() => setSaved(undefined), 3000)
      onSaved()
    } catch (e) {
      setError(errorText(e))
    }
  }

  function saveFolder(e: FormEvent) {
    e.preventDefault()
    return save({ folder: path.trim() })
  }

  return (
    <SettingsGroup title="Automatic backups" note={error ? undefined : saved}>
      <ToggleRow
        label="Back up every night"
        help={`At half past three. The last ${keep} are kept, and a copy is always made before an upgrade changes the database.`}
        checked={nightly}
        onChange={(on) => void save({ nightly: on })}
      />
      <form className="setting-row static backup-folder" onSubmit={(e) => void saveFolder(e)}>
        <span className="setting-text">
          <strong>Folder</strong>
          <span className="muted">A folder on another drive, or a share, survives this one failing. Empty for the data folder.</span>
        </span>
        <span className="backup-folder-field">
          <input type="text" value={path} onChange={(e) => setPath(e.target.value)} placeholder={folder ? undefined : folderInUse} aria-label="Backups folder" />
          <AsyncButton type="submit" className="button quiet small" disabled={path.trim() === (folder ?? '')}>
            Save
          </AsyncButton>
        </span>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </SettingsGroup>
  )
}

/** Asks for an optional passphrase before saving a file that holds the server's keys. */
function PassphraseDialog({ title, onSave, onClose }: { title: string; onSave: (passphrase?: string) => Promise<void>; onClose: () => void }) {
  const [passphrase, setPassphrase] = useState('')
  const [again, setAgain] = useState('')
  const [error, setError] = useState<string>()

  async function save() {
    if (passphrase && passphrase.length < 8) return setError('Use a passphrase of at least 8 characters, or none.')
    if (passphrase !== again) return setError('The passphrases don’t match.')
    setError(undefined)
    try {
      await onSave(passphrase || undefined)
      onClose()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <ConfirmDialog title={title} confirmLabel="Save file" error={error} onConfirm={save} onClose={onClose}>
      <p>
        The file holds the server’s keys and the passwords for Sonarr, Radarr and the like. A passphrase keeps them safe if the file goes astray; without one,
        keep the file somewhere private.
      </p>
      <label className="field">
        <span className="field-label">Passphrase (optional)</span>
        <input type="password" autoComplete="new-password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
      </label>
      {passphrase && (
        <label className="field">
          <span className="field-label">Passphrase again</span>
          <input type="password" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} />
        </label>
      )}
      {passphrase && <p className="muted">Without it the file can’t be opened, by anyone. Flexarr doesn’t keep it.</p>}
    </ConfirmDialog>
  )
}

/** The last word before a restore: what goes, and the main profile's PIN when it has one. */
function RestoreDialog({
  createdAt,
  mainHasPin,
  onRestore,
  onRestarting,
  onClose,
}: {
  createdAt: string
  mainHasPin: boolean
  onRestore: (confirmation: { pin?: string; confirm: boolean }) => Promise<void>
  onRestarting: () => void
  onClose: () => void
}) {
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()

  async function restore() {
    if (mainHasPin && !pin) return setError('Enter the main profile’s PIN.')
    setError(undefined)
    try {
      await onRestore({ pin: pin || undefined, confirm: true })
      onRestarting()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <ConfirmDialog title="Restore this backup?" confirmLabel="Restore and restart" danger error={error} onConfirm={restore} onClose={onClose}>
      <p>
        Everything on this server goes back to how it was on {when(createdAt)}: profiles, what’s been watched, settings, keys and sign-ins. Anything since
        is lost, though a copy of the server as it is now is kept first. Flexarr restarts to finish, and anyone watching is stopped.
      </p>
      {mainHasPin && (
        <label className="field">
          <span className="field-label">Main profile’s PIN</span>
          <input type="password" inputMode="numeric" autoComplete="off" value={pin} onChange={(e) => setPin(e.target.value)} />
        </label>
      )}
    </ConfirmDialog>
  )
}

/** A backup or settings file from this computer: opened (with its passphrase), looked over, then restored or imported. */
function FromFile({ mainHasPin, onRestore }: { mainHasPin: boolean; onRestore: (upload: BackupUpload, contents: BackupContents) => void }) {
  const input = useRef<HTMLInputElement>(null)
  const [upload, setUpload] = useState<BackupUpload>()
  const [fileName, setFileName] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [passphrase, setPassphrase] = useState('')

  async function choose(file: File | undefined) {
    if (!file) return
    if (upload) void forgetBackupFile(upload.id).catch(() => undefined)
    setUpload(undefined)
    setError(undefined)
    setFileName(file.name)
    setBusy(true)
    try {
      setUpload(await uploadBackupFile(file))
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
      if (input.current) input.current.value = ''
    }
  }

  async function open(e: FormEvent) {
    e.preventDefault()
    if (!upload) return
    setError(undefined)
    try {
      setUpload(await openBackupFile(upload.id, passphrase))
      setPassphrase('')
    } catch (err) {
      setError(errorText(err))
    }
  }

  function done() {
    if (upload) void forgetBackupFile(upload.id).catch(() => undefined)
    setUpload(undefined)
    setFileName(undefined)
    setError(undefined)
  }

  return (
    <SettingsGroup title="From a file" note="A backup or settings file from this server or another Flexarr.">
      <SettingRow as="div" label={fileName ?? 'Restore or import'} help={fileName ? undefined : 'Choose a backup to restore, or a settings file to import.'}>
        <input ref={input} type="file" hidden accept=".zip,.json,.flexarr-backup,.flexarr-settings" onChange={(e) => void choose(e.target.files?.[0])} />
        <AsyncButton className="button quiet small" busy={busy} onClick={() => input.current?.click()}>
          {fileName ? 'Choose another…' : 'Choose a file…'}
        </AsyncButton>
      </SettingRow>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {upload && upload.kind === null && (
        <form className="setting-row static backup-folder" onSubmit={(e) => void open(e)}>
          <span className="setting-text">
            <strong>Passphrase</strong>
            <span className="muted">This file is protected. Enter the passphrase it was saved with.</span>
          </span>
          <span className="backup-folder-field">
            <input type="password" autoComplete="off" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} aria-label="Passphrase" autoFocus />
            <AsyncButton type="submit" className="button small" disabled={!passphrase}>
              Open
            </AsyncButton>
          </span>
        </form>
      )}
      {upload?.backup && (
        <UploadedBackup contents={upload.backup} fromAnotherServer={upload.fromAnotherServer} onRestore={() => onRestore(upload, upload.backup!)} onCancel={done} />
      )}
      {upload?.settings && <UploadedSettings id={upload.id} file={upload.settings} mainHasPin={mainHasPin} onCancel={done} />}
    </SettingsGroup>
  )
}

function UploadedBackup({ contents, fromAnotherServer, onRestore, onCancel }: { contents: BackupContents; fromAnotherServer: boolean; onRestore: () => void; onCancel: () => void }) {
  const count = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`
  return (
    <div className="backup-file">
      <p>
        A backup from {when(contents.createdAt)}, made by Flexarr {contents.flexarrVersion}: {count(contents.profiles, 'profile', 'profiles')},{' '}
        {count(contents.libraries, 'library', 'libraries')} and {count(contents.titles, 'title', 'titles')}.
      </p>
      {contents.problem && (
        <p className="error" role="alert">
          {contents.problem}
        </p>
      )}
      {!contents.problem && fromAnotherServer && (
        <p className="error">
          This backup is from another Flexarr server. Restoring it brings that server’s keys and the browsers and devices signed in to it, so only
          restore one you made yourself. This machine’s paths (ffmpeg, the cache) are kept as they are.
        </p>
      )}
      <div className="form-actions">
        <button className="button quiet small" onClick={onCancel}>
          Cancel
        </button>
        <button className="button small danger" disabled={!!contents.problem} onClick={onRestore}>
          Restore…
        </button>
      </div>
    </div>
  )
}

function UploadedSettings({ id, file, mainHasPin, onCancel }: { id: string; file: SettingsFile; mainHasPin: boolean; onCancel: () => void }) {
  const offered = sections.filter((s) => file.sections.includes(s.id))
  const [chosen, setChosen] = useState(() => new Set(offered.filter((s) => !s.off).map((s) => s.id)))
  const [libraries, setLibraries] = useState(() =>
    file.libraries.map((l) => ({ ...l, include: !l.alreadyHere, paths: l.folders.map((f) => f.path) })),
  )
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()
  const [result, setResult] = useState<SettingsImportResult>()

  const toggle = (section: string, on: boolean) =>
    setChosen((current) => {
      const next = new Set(current)
      if (on) next.add(section)
      else next.delete(section)
      return next
    })

  async function apply() {
    setError(undefined)
    try {
      setResult(
        await importSettingsFile(id, {
          sections: [...chosen],
          libraries: libraries.filter((l) => l.include).map((l) => ({ name: l.name, folders: l.paths })),
          pin: pin || undefined,
          confirm: true,
        }),
      )
    } catch (e) {
      setError(errorText(e))
    }
  }

  if (result)
    return (
      <div className="backup-file" role="status">
        <p>
          {result.applied.length > 0 ? `Imported ${result.applied.map(sectionLabel).join(', ')}.` : 'Nothing was imported.'}
          {result.librariesAdded > 0 && ` ${result.librariesAdded === 1 ? 'The new library is' : `The ${result.librariesAdded} new libraries are`} being scanned.`}
        </p>
        {result.problems.length > 0 && (
          <ul className="backup-problems">
            {result.problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        )}
        <div className="form-actions">
          <button className="button quiet small" onClick={onCancel}>
            Done
          </button>
        </div>
      </div>
    )

  return (
    <div className="backup-file">
      <p>
        Settings{file.exportedAt && ` exported ${when(file.exportedAt)}`}
        {file.flexarrVersion && ` by Flexarr ${file.flexarrVersion}`}. Each part you choose replaces this server’s.
      </p>
      {offered.map((s) => (
        <div key={s.id}>
          <label className="setting-row">
            <span className="setting-text">
              <strong>{s.label}</strong>
              <span className="muted">{s.help}</span>
            </span>
            <input type="checkbox" className="switch" role="switch" checked={chosen.has(s.id)} onChange={(e) => toggle(s.id, e.target.checked)} />
          </label>
          {s.id === 'libraries' && chosen.has('libraries') && (
            <div className="backup-libraries">
              {libraries.map((l, i) => (
                <fieldset key={l.name} disabled={l.alreadyHere}>
                  <label className="backup-library">
                    <input
                      type="checkbox"
                      checked={l.include}
                      onChange={(e) => setLibraries((all) => all.map((x, j) => (j === i ? { ...x, include: e.target.checked } : x)))}
                    />
                    <strong>{l.name}</strong>
                    <span className="muted">
                      {libraryTypeLabel[l.type]}
                      {l.alreadyHere && ' · already here'}
                    </span>
                  </label>
                  {l.include &&
                    l.paths.map((path, k) => {
                      const original = l.folders[k]
                      const missing = path === original.path && !original.exists
                      return (
                        <label key={original.path} className="field backup-library-folder">
                          <input
                            type="text"
                            value={path}
                            aria-label={`${l.name} folder`}
                            aria-invalid={missing || undefined}
                            onChange={(e) =>
                              setLibraries((all) => all.map((x, j) => (j === i ? { ...x, paths: x.paths.map((p, n) => (n === k ? e.target.value : p)) } : x)))
                            }
                          />
                          {missing && <span className="field-help">Not on this server: enter where it is here.</span>}
                        </label>
                      )
                    })}
                </fieldset>
              ))}
            </div>
          )}
        </div>
      ))}
      {(chosen.has('keys') || chosen.has('machine') || chosen.has('access')) && mainHasPin && (
        <label className="field">
          <span className="field-label">Main profile’s PIN</span>
          <input type="password" inputMode="numeric" autoComplete="off" value={pin} onChange={(e) => setPin(e.target.value)} />
        </label>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="form-actions">
        <button className="button quiet small" onClick={onCancel}>
          Cancel
        </button>
        <AsyncButton className="button small" disabled={chosen.size === 0} onClick={apply}>
          Import
        </AsyncButton>
      </div>
    </div>
  )
}

/** While the server restarts to finish a restore: waits for it to answer again, then reloads. */
function Restarting() {
  const [late, setLate] = useState(false)

  useEffect(() => {
    let stopped = false
    void (async () => {
      const start = Date.now()
      let wentDown = false
      while (!stopped && Date.now() - start < 120_000) {
        await new Promise((done) => setTimeout(done, 2000))
        try {
          const response = await fetch('/health', { cache: 'no-store' })
          if (!response.ok) wentDown = true
          // Answering again after going away, or answering all along (a restart too quick to see).
          else if (wentDown || Date.now() - start > 10_000) {
            window.location.reload()
            return
          }
        } catch {
          wentDown = true
        }
        if (Date.now() - start > 60_000) setLate(true)
      }
      setLate(true)
    })()
    return () => {
      stopped = true
    }
  }, [])

  return (
    <section>
      <SettingsHeader title="Restoring">Flexarr is restarting with the backup. This page reloads when it’s back.</SettingsHeader>
      {late && (
        <p className="muted" role="status">
          It’s taking a while. If nothing starts Flexarr again by itself on this machine, start it again, and the backup is put in place as it starts.
        </p>
      )}
    </section>
  )
}
