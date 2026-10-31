import { ConnectTv } from '../../components/ConnectTv'
import { WaitingPairings } from '../../components/WaitingPairings'
import { SettingsHeader } from './shared'

export function ConnectTvSection() {
  return (
    <section>
      <SettingsHeader title="Connect a device" back={{ to: '/settings/devices', label: 'Devices' }} />
      {/* Devices already asking come first: allowing one there needs no code typed. */}
      <WaitingPairings />
      <ConnectTv titled={false} />
    </section>
  )
}
