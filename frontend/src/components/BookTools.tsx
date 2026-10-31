import { useEffect, useState, type MouseEvent } from 'react'
import type { AudioPlayer } from '../hooks/audioPlayerContext'
import { useAudioTime } from '../hooks/audioPlayerContext'
import { bookSpeedPresets, chapterEnd, chapterIndexAt, formatSpeed, stepSpeed } from '../lib/books'
import { formatClock } from '../lib/format'
import { showNotice } from '../lib/notice'
import { bookSleepChoices, minutesLeft, sleepLabel } from '../lib/sleepTimer'
import { Bookmark, Sleep } from './icons'
import { Menu, type MenuPoint } from './ItemMenu'

/** A book's speed, sleep timer and bookmark buttons, each opening a menu or doing its one thing. */
export function BookTools({ player }: { player: AudioPlayer }) {
  const time = useAudioTime()
  const [open, setOpen] = useState<{ menu: 'speed' | 'sleep'; at: MenuPoint } | null>(null)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!player.sleep || !('until' in player.sleep)) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [player.sleep])
  const toggle = (menu: 'speed' | 'sleep') => (e: MouseEvent<HTMLButtonElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    setOpen(open?.menu === menu ? null : { menu, at: { x: r.right - 220, y: r.bottom + 6 } })
  }
  const book = player.book
  const chapters = book?.chapters ?? []
  const chapter = chapterIndexAt(chapters, time)
  const end = chapter >= 0 && book ? chapterEnd(chapters, chapter, book.durationSeconds) : null
  const left = minutesLeft(player.sleep, now)
  const label = sleepLabel(player.sleep, now, 'chapter')
  const speed = player.speed

  return (
    <>
      <button
        className={speed === 1 ? 'icon-button now-playing-speed' : 'icon-button now-playing-speed active'}
        aria-label={`Speed, ${formatSpeed(speed)}`}
        aria-haspopup="menu"
        aria-expanded={open?.menu === 'speed'}
        onClick={toggle('speed')}
      >
        {formatSpeed(speed)}
      </button>
      <button
        className={player.sleep ? 'icon-button now-playing-sleep active' : 'icon-button now-playing-sleep'}
        aria-label={label}
        title={label}
        aria-haspopup="menu"
        aria-expanded={open?.menu === 'sleep'}
        onClick={toggle('sleep')}
      >
        <Sleep />
        {left !== null && <span className="now-playing-count" aria-hidden="true">{left}</span>}
      </button>
      <button
        className="icon-button now-playing-bookmark"
        aria-label="Add a bookmark"
        title="Add a bookmark"
        disabled={!book}
        onClick={() =>
          player.addBookmark().then(
            () => showNotice(`Bookmarked ${formatClock(time)}`),
            () => showNotice('That didn’t work. Try again.'),
          )
        }
      >
        <Bookmark />
      </button>
      {open?.menu === 'speed' && (
        <Menu
          label="Speed"
          at={open.at}
          actions={[
            { label: `Slower (${formatSpeed(stepSpeed(speed, -1))})`, run: () => player.setSpeed(stepSpeed(speed, -1)) },
            { label: `Faster (${formatSpeed(stepSpeed(speed, 1))})`, run: () => player.setSpeed(stepSpeed(speed, 1)) },
            'divider',
            ...bookSpeedPresets.map((s) => ({ label: `${formatSpeed(s)}${s === speed ? ' ✓' : ''}`, run: () => player.setSpeed(s) })),
          ]}
          onClose={() => setOpen(null)}
        />
      )}
      {open?.menu === 'sleep' && (
        <Menu
          label="Sleep timer"
          at={open.at}
          actions={[
            ...bookSleepChoices(end).map((choice) => ({ label: choice.label, run: () => player.setSleep(choice.start()) })),
            ...(player.sleep ? ['divider' as const, { label: 'Turn off', run: () => player.setSleep(null) }] : []),
          ]}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  )
}
