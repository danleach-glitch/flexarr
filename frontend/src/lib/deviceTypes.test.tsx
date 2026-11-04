import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { DeviceTypeIcon } from '../components/DeviceTypeIcon'
import { deviceTypeName } from './deviceTypes'

describe('device types', () => {
  it('names the Mac app as a Mac, and leaves a kind newer than this page unnamed', () => {
    expect(deviceTypeName('Mac')).toBe('Mac')
    expect(deviceTypeName('IPhone')).toBe('iPhone')
    expect(deviceTypeName('Toaster' as never)).toBeUndefined()
    expect(deviceTypeName(null)).toBeUndefined()
  })

  it('draws a Mac as a computer, not a TV', () => {
    const mac = render(<DeviceTypeIcon type="Mac" />).container.innerHTML
    const tv = render(<DeviceTypeIcon type="AppleTv" />).container.innerHTML
    expect(mac).not.toBe(tv)
    expect(mac).toContain('<rect x="3" y="4" width="18" height="12"')
  })
})
