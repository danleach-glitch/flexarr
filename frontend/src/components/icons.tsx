import type { SVGProps } from 'react'

const base = { viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true } as const

type IconProps = SVGProps<SVGSVGElement>

export const Play = (p: IconProps) => (
  <svg {...base} {...p} fill="currentColor" stroke="none">
    <path d="M7 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5Z" />
  </svg>
)
export const Pause = (p: IconProps) => (
  <svg {...base} {...p} fill="currentColor" stroke="none">
    <rect x="5" y="4" width="5" height="16" rx="1.2" />
    <rect x="14" y="4" width="5" height="16" rx="1.2" />
  </svg>
)
export const Info = (p: IconProps) => (
  <svg {...base} {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 11v5M12 8h.01" />
  </svg>
)
export const Check = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="m5 12.5 4.5 4.5L19 7.5" />
  </svg>
)
export const Search = (p: IconProps) => (
  <svg {...base} {...p}>
    <circle cx="11" cy="11" r="7" />
    <path d="m20 20-3.5-3.5" />
  </svg>
)
export const Gear = (p: IconProps) => (
  <svg {...base} {...p}>
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z" />
  </svg>
)
export const Back = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M15 5l-7 7 7 7" />
  </svg>
)
export const ChevronLeft = Back
export const ChevronRight = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="m9 5 7 7-7 7" />
  </svg>
)
export const Close = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M6 6l12 12M18 6 6 18" />
  </svg>
)
export const Volume = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 9v6h4l5 4V5L8 9H4Z" fill="currentColor" stroke="none" />
    <path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12" />
  </svg>
)
export const Muted = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 9v6h4l5 4V5L8 9H4Z" fill="currentColor" stroke="none" />
    <path d="m17 9 5 6M22 9l-5 6" />
  </svg>
)
export const Fullscreen = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5" />
  </svg>
)
export const ExitFullscreen = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5" />
  </svg>
)
export const Captions = (p: IconProps) => (
  <svg {...base} {...p}>
    <rect x="3" y="5" width="18" height="14" rx="2.5" />
    <path d="M10 10.5a2.5 2.5 0 1 0 0 3M17 10.5a2.5 2.5 0 1 0 0 3" />
  </svg>
)
export const Replay10 = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 12a8 8 0 1 0 2.4-5.7L4 8.5" />
    <path d="M4 4v4.5h4.5" />
    <text x="12" y="15.5" fontSize="7" textAnchor="middle" fill="currentColor" stroke="none" fontWeight="700">10</text>
  </svg>
)
export const Forward10 = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M20 12a8 8 0 1 1-2.4-5.7L20 8.5" />
    <path d="M20 4v4.5h-4.5" />
    <text x="12" y="15.5" fontSize="7" textAnchor="middle" fill="currentColor" stroke="none" fontWeight="700">10</text>
  </svg>
)
export const Next = (p: IconProps) => (
  <svg {...base} {...p} fill="currentColor" stroke="none">
    <path d="M5 5.5v13a1 1 0 0 0 1.6.8l9-6.5a1 1 0 0 0 0-1.6l-9-6.5A1 1 0 0 0 5 5.5Z" />
    <rect x="17" y="5" width="2.6" height="14" rx="1.2" />
  </svg>
)
export const Film = (p: IconProps) => (
  <svg {...base} {...p}>
    <rect x="3" y="4" width="18" height="16" rx="2" />
    <path d="M7 4v16M17 4v16M3 9h4M3 15h4M17 9h4M17 15h4" />
  </svg>
)
export const More = (p: IconProps) => (
  <svg {...base} {...p} fill="currentColor" stroke="none">
    <circle cx="5.5" cy="12" r="2" />
    <circle cx="12" cy="12" r="2" />
    <circle cx="18.5" cy="12" r="2" />
  </svg>
)
export const ChevronUp = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="m6 15 6-6 6 6" />
  </svg>
)
export const ChevronDown = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="m6 9 6 6 6-6" />
  </svg>
)
export const Folder = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
  </svg>
)
export const FolderUp = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
    <path d="M12 16v-5M9.5 13.5 12 11l2.5 2.5" />
  </svg>
)
export const Home = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 10.5 12 4l8 6.5V19a1 1 0 0 1-1 1h-4.5v-6h-5v6H5a1 1 0 0 1-1-1Z" />
  </svg>
)
export const Tv = (p: IconProps) => (
  <svg {...base} {...p}>
    <rect x="3" y="6" width="18" height="13" rx="2" />
    <path d="m8.5 2.5 3.5 3.5 3.5-3.5" />
  </svg>
)
export const Music = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M9 18V5l11-2v13" />
    <circle cx="6.5" cy="18" r="2.5" />
    <circle cx="17.5" cy="16" r="2.5" />
  </svg>
)
export const Stack = (p: IconProps) => (
  <svg {...base} {...p}>
    <rect x="3" y="9" width="18" height="11" rx="2" />
    <path d="M5.5 5.5h13M8 2.5h8" />
  </svg>
)
export const Compass = (p: IconProps) => (
  <svg {...base} {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="m15.5 8.5-2 5-5 2 2-5Z" />
  </svg>
)
export const Calendar = (p: IconProps) => (
  <svg {...base} {...p}>
    <rect x="3.5" y="5" width="17" height="15" rx="2" />
    <path d="M3.5 10h17M8 3v4M16 3v4" />
  </svg>
)
export const Menu = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M4 6h16M4 12h16M4 18h16" />
  </svg>
)
/** Two chevrons pointing left: collapses the sidebar (flipped, expands it). */
export const ChevronsLeft = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="m11 17-5-5 5-5M18 17l-5-5 5-5" />
  </svg>
)
export const External = (p: IconProps) => (
  <svg {...base} {...p}>
    <path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
  </svg>
)
