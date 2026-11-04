import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  backUpNow,
  deleteBackup,
  downloadBackup,
  exportSettings,
  forgetBackupFile,
  getBackups,
  getMe,
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
import { FolderPicker } from '../../components/FolderPicker'
import { Download, Restore } from '../../components/icons'
import { icon } from '../../components/icons.generated'
import { useApi } from '../../hooks/useApi'
import { useChoosesOnMac } from '../../hooks/useChoosesOnMac'
import { formatSize, libraryTypeLabel } from '../../lib/format'
import { SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'
import { locale, t, tk, Words } from '../../i18n'

const kindLabel: Record<BackupKind, string> = {
  Manual: tk('Made by hand'),
  Nightly: tk('Nightly'),
  PreUpgrade: tk('Before an upgrade'),
  PreRestore: tk('Before a restore'),
}

/** The parts of a settings file, in the order they're offered. Server keys and this machine's paths start unticked. */
const sections: { id: string; label: string; help: string; off?: boolean }[] = [
  { id: 'libraries', label: Words.libraries, help: tk('Added if there’s none of that name here, then scanned.') },
  { id: 'connections', label: tk('Connections'), help: tk('Sonarr, Radarr, Lidarr, Seerr and path mappings, with their keys.') },
  { id: 'liveTv', label: Words.liveTv, help: tk('The Dispatcharr source and the DVR’s settings.') },
  { id: 'streaming', label: tk('Streaming'), help: tk('Hardware acceleration and limits.') },
  { id: 'layout', label: tk('Home & browse'), help: tk('The rows for profiles that haven’t chosen their own.') },
  { id: 'podcasts', label: Words.podcasts, help: tk('The country for charts.') },
  { id: 'notifications', label: tk('Notifications'), help: tk('Push notifications to phones.') },
  { id: 'access', label: tk('Access'), help: tk('When to ask for the API key, and trusted proxies. Asks for the admin PIN.') },
  { id: 'machine', label: tk('This machine’s paths'), help: tk('ffmpeg, the cache and backups folders. Usually wrong on another machine, and it asks for the admin PIN.'), off: true },
  {
    id: 'keys',
    label: tk('Server keys'),
    help: tk('The API key and webhook secret. Apps and Sonarr or Radarr set up with the old server’s keep working; anything using this server’s current key needs the new one.'),
    off: true,
  },
]

const sectionLabel = (id: string) => {
  const label = sections.find((s) => s.id === id)?.label
  return label ? t(label) : id
}

function when(iso: string) {
  const date = new Date(iso)
  const thisYear = date.getFullYear() === new Date().getFullYear()
  return date.toLocaleString(locale(), { weekday: 'short', day: 'numeric', month: 'short', year: thisYear ? undefined : 'numeric', hour: '2-digit', minute: '2-digit' })
}

type Dialog =
  | { kind: 'download'; backup: Backup }
  | { kind: 'export' }
  | { kind: 'delete'; backup: Backup }
  | { kind: 'restore'; createdAt: string; name?: string; uploadId?: string }

export function BackupSection() {
  const backups = useApi(getBackups, [])
  // Restoring keys and access settings asks for the admin PIN, when there is one.
  const me = useApi(getMe, [])
  // Until /me answers, assume there's an admin PIN: asking for it beats a refusal from the server.
  const mainHasPin = me.data?.adminPinMissing !== true
  const [dialog, setDialog] = useState<Dialog>()
  const [deleteError, setDeleteError] = useState<string>()
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
      <SettingsHeader title={t('Backup & restore')}>
        {t('Everything Flexarr can’t rebuild from your media: profiles, watch history, playlists, collections, settings and sign-ins.')}
      </SettingsHeader>

      <SettingsGroup
        title={t('Backups')}
        action={
          <AsyncButton className="button small" onClick={makeOne}>
            {t('Back up now')}
          </AsyncButton>
        }
        note={
          backups.data && (
            <>
              {t.rich('Kept in {folder}.', { folder: <code>{backups.data.folder}</code> })}
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
        {backups.loading && !backups.data && <p className="muted">{t('Loading…')}</p>}
        {backups.error && (
          <p className="error" role="alert">
            {t('Couldn’t list the backups: {message}', { message: backups.error.message })}
          </p>
        )}
        {backups.data?.backups.length === 0 && <p className="muted">{t('No backups yet.')}</p>}
        {backups.data?.backups.map((b) => (
          <div className="library-row backup-row" key={b.name}>
            <div>
              <h3>{when(b.createdAt)}</h3>
              <p className="muted">
                {t(kindLabel[b.kind])}{'\u2003'}{formatSize(b.size)}
                {b.flexarrVersion && `\u2003Flexarr ${b.flexarrVersion}`}
              </p>
            </div>
            <div className="library-row-actions">
              <IconButton label={t(Words.download)} icon={<Download />} onClick={() => setDialog({ kind: 'download', backup: b })} />
              <IconButton label={t('Restore')} icon={<Restore />} onClick={() => setDialog({ kind: 'restore', createdAt: b.createdAt, name: b.name })} />
              <IconButton label={t(Words.delete)} icon={<icon.delete />} className="button quiet small icon danger-icon" onClick={() => setDialog({ kind: 'delete', backup: b })} />
            </div>
          </div>
        ))}
      </SettingsGroup>

      {backups.data && <Automatic nightly={backups.data.nightly} keep={backups.data.keepNightly} folder={backups.data.folderSetting} folderInUse={backups.data.folder} onSaved={backups.reload} />}

      <SettingsGroup title={t('Settings file')} note={t('Libraries and server settings, without profiles or history. Handy for setting up another server.')}>
        <SettingRow as="div" label={t('Export settings')} help={t('Saves a file to this computer.')}>
          <button className="button quiet small" onClick={() => setDialog({ kind: 'export' })}>
            {t('Export…')}
          </button>
        </SettingRow>
      </SettingsGroup>

      <FromFile mainHasPin={mainHasPin} onRestore={(upload, contents) => setDialog({ kind: 'restore', createdAt: contents.createdAt, uploadId: upload.id })} />

      {(dialog?.kind === 'download' || dialog?.kind === 'export') && (
        <PassphraseDialog
          title={dialog.kind === 'export' ? t('Export settings') : t('Download this backup')}
          mainHasPin={mainHasPin}
          onSave={(confirmation, passphrase) =>
            dialog.kind === 'export' ? exportSettings(confirmation, passphrase) : downloadBackup(dialog.backup.name, confirmation, passphrase)
          }
          onClose={() => setDialog(undefined)}
        />
      )}
      {dialog?.kind === 'delete' && (
        <ConfirmDialog
          title={t('Delete this backup?')}
          confirmLabel={t(Words.delete)}
          danger
          error={deleteError}
          onConfirm={async () => {
            setDeleteError(undefined)
            try {
              await deleteBackup(dialog.backup.name)
              setDialog(undefined)
              backups.reload()
            } catch (e) {
              setDeleteError(errorText(e))
            }
          }}
          onClose={() => {
            setDialog(undefined)
            setDeleteError(undefined)
          }}
        >
          <p>
            {t('{kind}, {date}.', { kind: t(kindLabel[dialog.backup.kind]), date: when(dialog.backup.createdAt) })}
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
  const [choosing, setChoosing] = useState(false)
  // The Mac app reaches only folders chosen in macOS's folder window: there the folder is chosen, never typed.
  const onMac = useChoosesOnMac()

  async function save(update: { folder?: string; nightly?: boolean }) {
    setError(undefined)
    try {
      await saveBackupSettings(update)
      setSaved(t('Saved'))
      setTimeout(() => setSaved(undefined), 3000)
      onSaved()
      return true
    } catch (e) {
      setError(errorText(e))
      return false
    }
  }

  function saveFolder(e: FormEvent) {
    e.preventDefault()
    return save({ folder: path.trim() })
  }

  return (
    <SettingsGroup title={t('Automatic backups')} note={error ? undefined : saved}>
      <ToggleRow
        label={t('Back up every night')}
        help={t('At half past three. The last {keep, number} are kept, and a copy is always made before an upgrade changes the database.', { keep })}
        checked={nightly}
        onChange={(on) => void save({ nightly: on })}
      />
      {onMac ? (
        <SettingRow
          as="div"
          label={t('Folder')}
          help={
            <>
              {t('A folder on another drive, or a share, survives this one failing.')}
              <code className="folder">{folder ?? folderInUse}</code>
            </>
          }
        >
          <span className="folder-field">
            {folder && (
              <button type="button" className="button quiet small" onClick={() => void save({ folder: '' })}>
                {t('Use the data folder')}
              </button>
            )}
            {!choosing && (
              <button type="button" className="button quiet small" onClick={() => setChoosing(true)}>
                {t('Change')}
              </button>
            )}
          </span>
        </SettingRow>
      ) : (
        <form className="setting-row static backup-folder" onSubmit={(e) => void saveFolder(e)}>
          <span className="setting-text">
            <strong>{t('Folder')}</strong>
            <span className="muted">{t('A folder on another drive, or a share, survives this one failing. Empty for the data folder.')}</span>
          </span>
          <span className="backup-folder-field">
            <input type="text" value={path} onChange={(e) => setPath(e.target.value)} placeholder={folder ? undefined : folderInUse} aria-label={t('Backups folder')} />
            {!choosing && (
              <button type="button" className="button quiet small" onClick={() => setChoosing(true)}>
                {t('Browse…')}
              </button>
            )}
            <AsyncButton type="submit" className="button quiet small" disabled={path.trim() === (folder ?? '')}>
              {t('Save')}
            </AsyncButton>
          </span>
        </form>
      )}
      {choosing && (
        <div className="podcast-folder-picker">
          <FolderPicker
            onPick={(picked) => {
              setPath(picked)
              void save({ folder: picked }).then((ok) => ok && setChoosing(false))
            }}
          />
          <button type="button" className="text-button" onClick={() => setChoosing(false)}>
            {t(Words.cancel)}
          </button>
        </div>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </SettingsGroup>
  )
}

/** Asks for an optional passphrase, and the admin PIN when it has one, before saving a file that holds the server's keys. */
function PassphraseDialog({
  title,
  mainHasPin,
  onSave,
  onClose,
}: {
  title: string
  mainHasPin: boolean
  onSave: (confirmation: { pin?: string; confirm: boolean }, passphrase?: string) => Promise<void>
  onClose: () => void
}) {
  const [passphrase, setPassphrase] = useState('')
  const [again, setAgain] = useState('')
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()

  async function save() {
    if (passphrase && passphrase.length < 8) return setError(t('Use a passphrase of at least 8 characters, or none.'))
    if (passphrase !== again) return setError(t('The passphrases don’t match.'))
    if (mainHasPin && !pin) return setError(t('Enter the admin PIN.'))
    setError(undefined)
    try {
      await onSave({ pin: pin || undefined, confirm: true }, passphrase || undefined)
      onClose()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <ConfirmDialog title={title} confirmLabel={t('Save file')} error={error} onConfirm={save} onClose={onClose}>
      <p>
        {t('The file holds the server’s keys and the passwords for Sonarr, Radarr and the like. A passphrase keeps them safe if the file goes astray; without one, keep the file somewhere private.')}
      </p>
      <label className="field">
        <span className="field-label">{t('Passphrase (optional)')}</span>
        <input type="password" autoComplete="new-password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
      </label>
      {passphrase && (
        <label className="field">
          <span className="field-label">{t('Passphrase again')}</span>
          <input type="password" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} />
        </label>
      )}
      {passphrase && <p className="muted">{t('Without it the file can’t be opened, by anyone. Flexarr doesn’t keep it.')}</p>}
      {mainHasPin && (
        <label className="field">
          <span className="field-label">{t('Admin PIN')}</span>
          <input type="password" inputMode="numeric" autoComplete="off" value={pin} onChange={(e) => setPin(e.target.value)} />
        </label>
      )}
    </ConfirmDialog>
  )
}

/** The last word before a restore: what goes, and the admin PIN when it has one. */
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
    if (mainHasPin && !pin) return setError(t('Enter the admin PIN.'))
    setError(undefined)
    try {
      await onRestore({ pin: pin || undefined, confirm: true })
      onRestarting()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <ConfirmDialog title={t('Restore this backup?')} confirmLabel={t('Restore and restart')} danger error={error} onConfirm={restore} onClose={onClose}>
      <p>
        {t('Everything on this server goes back to how it was on {date}: profiles, what’s been watched, settings, keys and sign-ins. Anything since is lost, though a copy of the server as it is now is kept first. Flexarr restarts to finish, and anyone watching is stopped.', { date: when(createdAt) })}
      </p>
      {mainHasPin && (
        <label className="field">
          <span className="field-label">{t('Admin PIN')}</span>
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
    <SettingsGroup title={t('From a file')} note={t('A backup or settings file from this server or another Flexarr.')}>
      <SettingRow as="div" label={fileName ?? t('Restore or import')} help={fileName ? undefined : t('Choose a backup to restore, or a settings file to import.')}>
        <input ref={input} type="file" hidden accept=".zip,.json,.flexarr-backup,.flexarr-settings" onChange={(e) => void choose(e.target.files?.[0])} />
        <AsyncButton className="button quiet small" busy={busy} onClick={() => input.current?.click()}>
          {fileName ? t('Choose another…') : t('Choose a file…')}
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
            <strong>{t('Passphrase')}</strong>
            <span className="muted">{t('This file is protected. Enter the passphrase it was saved with.')}</span>
          </span>
          <span className="backup-folder-field">
            <input type="password" autoComplete="off" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} aria-label={t('Passphrase')} autoFocus />
            <AsyncButton type="submit" className="button small" disabled={!passphrase}>
              {t('Open')}
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
  return (
    <div className="backup-file">
      <p>
        {t('A backup from {date}, made by Flexarr {version}: {profiles, plural, one {# profile} other {# profiles}}, {libraries, plural, one {# library} other {# libraries}} and {titles, plural, one {# title} other {# titles}}.', {
          date: when(contents.createdAt),
          version: contents.flexarrVersion,
          profiles: contents.profiles,
          libraries: contents.libraries,
          titles: contents.titles,
        })}
      </p>
      {contents.problem && (
        <p className="error" role="alert">
          {contents.problem}
        </p>
      )}
      {!contents.problem && fromAnotherServer && (
        <p className="error">
          {t('This backup is from another Flexarr server. Restoring it brings that server’s keys and the browsers and devices signed in to it, so only restore one you made yourself. This machine’s paths (ffmpeg, the cache) are kept as they are.')}
        </p>
      )}
      <div className="form-actions">
        <button className="button quiet small" onClick={onCancel}>
          {t(Words.cancel)}
        </button>
        <button className="button small danger" disabled={!!contents.problem} onClick={onRestore}>
          {t('Restore…')}
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
          {result.applied.length > 0 ? t('Imported {parts}.', { parts: result.applied.map(sectionLabel).join(', ') }) : t('Nothing was imported.')}
          {result.librariesAdded > 0 && ` ${t('{count, plural, one {The new library is being scanned.} other {The # new libraries are being scanned.}}', { count: result.librariesAdded })}`}
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
            {t(Words.done)}
          </button>
        </div>
      </div>
    )

  return (
    <div className="backup-file">
      <p>
        {file.exportedAt && file.flexarrVersion
          ? t('Settings exported {date} by Flexarr {version}. Each part you choose replaces this server’s.', { date: when(file.exportedAt), version: file.flexarrVersion })
          : file.exportedAt
            ? t('Settings exported {date}. Each part you choose replaces this server’s.', { date: when(file.exportedAt) })
            : file.flexarrVersion
              ? t('Settings by Flexarr {version}. Each part you choose replaces this server’s.', { version: file.flexarrVersion })
              : t('Settings. Each part you choose replaces this server’s.')}
      </p>
      {offered.map((s) => (
        <div key={s.id}>
          <label className="setting-row">
            <span className="setting-text">
              <strong>{t(s.label)}</strong>
              <span className="muted">{t(s.help)}</span>
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
                      {l.alreadyHere && `\u2003${t('already here')}`}
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
                            aria-label={t('{name} folder', { name: l.name })}
                            aria-invalid={missing || undefined}
                            onChange={(e) =>
                              setLibraries((all) => all.map((x, j) => (j === i ? { ...x, paths: x.paths.map((p, n) => (n === k ? e.target.value : p)) } : x)))
                            }
                          />
                          {missing && <span className="field-help">{t('Not on this server: enter where it is here.')}</span>}
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
          <span className="field-label">{t('Admin PIN')}</span>
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
          {t(Words.cancel)}
        </button>
        <AsyncButton className="button small" disabled={chosen.size === 0} onClick={apply}>
          {t('Import')}
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
      <SettingsHeader title={t('Restoring')}>{t('Flexarr is restarting with the backup. This page reloads when it’s back.')}</SettingsHeader>
      {late && (
        <p className="muted" role="status">
          {t('It’s taking a while. If nothing starts Flexarr again by itself on this machine, start it again, and the backup is put in place as it starts.')}
        </p>
      )}
    </section>
  )
}
