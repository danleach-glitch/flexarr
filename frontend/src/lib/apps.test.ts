import { describe, expect, it } from 'vitest'
import { appLink } from './apps'

describe('appLink', () => {
  it('sends localhost to the host the browser reached Flexarr on, keeping the port and path', () => {
    expect(appLink('http://localhost:8989', 'mini.local')).toBe('http://mini.local:8989/')
    expect(appLink('http://127.0.0.1:7878/radarr', '192.168.1.106')).toBe('http://192.168.1.106:7878/radarr')
    expect(appLink('http://[::1]:5055', 'mini.local')).toBe('http://mini.local:5055/')
  })

  it('leaves other hosts alone', () => {
    expect(appLink('https://sonarr.example.com', 'mini.local')).toBe('https://sonarr.example.com/')
  })

  it('gives nothing for an address that isn’t a web address', () => {
    expect(appLink('', 'mini.local')).toBeUndefined()
    expect(appLink('localhost:8989', 'mini.local')).toBeUndefined()
    expect(appLink('javascript:alert(1)', 'mini.local')).toBeUndefined()
  })
})
