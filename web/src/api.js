import { reactive } from 'vue'

/** Global app state: the signed-in user (`Me` from the contract) or null. */
export const store = reactive({ user: null, loaded: false })

export const PLATFORMS = ['x86', 'x86_64', 'arm64', 'riscv64']
export const STATUSES = ['Queued', 'InProgress', 'Succeeded', 'Failed', 'Cancelled']

export class ApiError extends Error {
  constructor(status, message) {
    super(message)
    this.status = status
  }
}

/**
 * Calls the backend API (same-origin cookie auth).
 * @param {string} path  Path below /api, e.g. "/platforms".
 * @param {{method?: string, body?: any, text?: boolean}} opts  `text` returns the body as a string.
 * @returns {Promise<any>} Parsed JSON, text, or null for 204. Throws ApiError on non-2xx; a 401 also clears the user.
 */
export async function api(path, { method = 'GET', body, text = false } = {}) {
  const res = await fetch('/api' + path, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (res.status === 401) store.user = null
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const problem = await res.json()
      message = problem.detail || problem.title || message
    } catch { /* body was not problem+json; keep the status line */ }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return null
  return text ? res.text() : res.json()
}

export async function loadMe() {
  try {
    store.user = await api('/auth/me')
  } catch {
    store.user = null
  }
  store.loaded = true
}

export const isAdmin = () => !!store.user?.roles?.includes('Admin')

export function formatBytes(n) {
  if (n == null) return '—'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  let i = 0
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++ }
  return `${i ? n.toFixed(1) : n} ${units[i]}`
}

export const formatDate = (iso) => (iso ? new Date(iso).toLocaleString() : '—')

/** Only allow local, path-absolute return URLs (blocks open redirects like //evil.com). */
export const safeReturnUrl = (u) => (typeof u === 'string' && u.startsWith('/') && !u.startsWith('//') && !u.startsWith('/\\') ? u : '/')
