import { useEffect, useState, type MouseEvent } from 'react'
import { podcastSpeeds, type AudioPlayer } from '../hooks/audioPlayerContext'
import { speedLabel } from '../lib/podcasts'
import { minutesLeft, sleepChoices, sleepLabel } from '../lib/sleepTimer'
import { Sleep } from './icons'
import { Menu, type MenuPoint } from './ItemMenu'

/** A podcast's speed and sleep timer, each a button that opens a menu of choices. */
export function PodcastTools({ player }: { player: AudioPlayer }) {
  const [open, setOpen] = useState<{ menu: 'speed' | 'sleep'; at: MenuPoint } | null>(null)
  // The minutes left on the timer, kept current while it runs.
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!player.sleep || !('until' in player.sleep)) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [player.sleep])
  const toggle = (menu: 'speed' | 'sleep') => (e: MouseEvent<HTMLButtonElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    setOpen(open?.menu === menu ? null : { menu, at: { x: r.right - 200, y: r.bottom + 6 } })
  }
  const left = minutesLeft(player.sleep, now)
  const label = sleepLabel(player.sleep, now)

  return (
    <>
      <button className={player.speed === 1 ? 'icon-button now-playing-speed' : 'icon-button now-playing-speed active'} aria-label={`Speed, ${speedLabel(player.speed)}`} aria-haspopup="menu" aria-expanded={open?.menu === 'speed'} onClick={toggle('speed')}>
        {speedLabel(player.speed)}
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
      {open?.menu === 'speed' && (
        <Menu
          label="Speed"
          at={open.at}
          actions={podcastSpeeds.map((speed) => ({ label: `${speedLabel(speed)}${speed === player.speed ? ' ✓' : ''}`, run: () => player.setSpeed(speed) }))}
          onClose={() => setOpen(null)}
        />
      )}
      {open?.menu === 'sleep' && (
        <Menu
          label="Sleep timer"
          at={open.at}
          actions={[
            ...sleepChoices('episode').map((choice) => ({ label: choice.label, run: () => player.setSleep(choice.start()) })),
            ...(player.sleep ? ['divider' as const, { label: 'Turn off', run: () => player.setSleep(null) }] : []),
          ]}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  )
}
