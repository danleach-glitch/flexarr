/** Longest name the server takes. */
export const SERVER_NAME_MAX = 40

/** The browser tab's title: "Smith House – Flexarr", so tabs on different servers can be told apart. */
export function setServerTitle(name: string | null | undefined) {
  document.title = name && name !== 'Flexarr' ? `${name} – Flexarr` : 'Flexarr'
}
