// Dev-only mock of the backend contract on :8080 so the SPA can be checked without .NET.
// Run: npm run mock (then npx vite). Not imported by the app, so it never reaches the bundle.
// Sign in with any email and password "admin" (Admin) or anything else (Builder); "bad" -> 401.
// Registered accounts check their password; confirm/reset links are printed to this console.
// The authenticator code is always 123456.
import http from 'node:http'

const PLATFORMS = ['x86', 'x86_64', 'arm64', 'riscv64']
const ago = (h) => new Date(Date.now() - h * 3600e3).toISOString()
const sha = (s) => [...Array(64)].map((_, i) => '0123456789abcdef'[(s.charCodeAt(i % s.length) * (i + 7)) % 16]).join('')

let user = null // the signed-in account (an entry of `users`)
let pending2fa = null
const users = [
  { id: 'u-ada', email: 'ada@example.com', displayName: 'Ada', roles: ['Builder'], emailConfirmed: true, twoFactorEnabled: true, disabled: false, recovery: [] },
  { id: 'u-grace', email: 'grace@example.com', displayName: 'Grace Builder', roles: ['Builder'], emailConfirmed: false, twoFactorEnabled: false, disabled: true, recovery: [] },
]
const me = (u) => ({ id: u.id, email: u.email, displayName: u.displayName, roles: u.roles, twoFactorEnabled: u.twoFactorEnabled })
const findUser = (email) => users.find((u) => u.email.toLowerCase() === String(email || '').toLowerCase())
const newCodes = () => [...Array(10)].map(() => Math.random().toString(36).slice(2, 7) + '-' + Math.random().toString(36).slice(2, 7))
const SPA = 'http://localhost:5173'
let nextId = 100
const ports = [['gcc', '13.2.0'], ['python3.12', '3.12.4'], ['haikuporter', '1.2.7'], ['vim', '9.1.0'], ['curl', '8.9.1'], ['sqlite', '3.46.0'], ['libpng16', '1.6.43'], ['a_rather_long_package_name_for_wrapping_tests', '2024.11.17']]
const packages = PLATFORMS.flatMap((p, pi) => ports.slice(0, 8 - pi * 2).map(([n, v], i) => ({
  platform: p, fileName: `${n}-${v}-1-${p}.hpkg`, size: 12345 * (i + 1) ** 4, sha256: sha(n + p),
  buildDate: ago(i * 5 + 3), uploadedAt: ago(i * 5 + 2), recipeUpdatedAt: ago(i * 30 + 48), portName: n, builtBy: i % 2 ? 'Ada' : 'Grace Builder',
})))
const clients = [
  { id: 1, name: 'garage-rpi5', platform: 'arm64', owner: 'Ada', enabled: true, createdAt: ago(400), lastSeenAt: ago(0.1) },
  { id: 2, name: 'ci-x64', platform: 'x86_64', owner: 'Grace Builder', enabled: true, createdAt: ago(900), lastSeenAt: ago(30) },
]
const recipes = ports.map(([n, v], i) => ({ id: i + 1, portName: n, category: ['sys-devel', 'dev-lang', 'haiku-apps', 'app-editors', 'net-misc', 'dev-db', 'media-libs', 'misc'][i], version: v, recipePath: `x/${n}/${n}-${v}.recipe`, recipeUpdatedAt: ago(i * 50) }))
const job = (id, portName, version, platform, status, extra = {}) => ({
  id, portName, version, platform, status, queuedAt: ago(id), queuedBy: 'admin@renkuos.org', claimedBy: null, claimedAt: null,
  leaseExpiresAt: null, completedAt: null, attempts: 0, failureSummary: null, hasLog: false, packages: [], ...extra,
})
const jobs = [
  job(1, 'gcc', '13.2.0', 'riscv64', 'Failed', { attempts: 2, completedAt: ago(2), hasLog: true, failureSummary: 'configure: error: cannot compute suffix of object files: cannot compile' }),
  job(2, 'python3.12', '3.12.4', 'arm64', 'InProgress', { claimedBy: 'garage-rpi5', claimedAt: ago(1), leaseExpiresAt: ago(-71), attempts: 1 }),
  job(3, 'vim', '9.1.0', 'x86_64', 'Succeeded', { claimedBy: 'ci-x64', completedAt: ago(5), attempts: 1, hasLog: true, packages: ['vim-9.1.0-1-x86_64.hpkg'] }),
  job(4, 'curl', '8.9.1', 'x86', 'Queued'),
  job(5, 'sqlite', '3.46.0', 'arm64', 'Failed', { attempts: 1, completedAt: ago(8), hasLog: true, failureSummary: 'patch does not apply: sqlite-3.46.0.patchset' }),
  job(6, 'qt6_base', '6.7.2', 'arm64', 'Succeeded', { claimedBy: 'garage-rpi5', completedAt: ago(9), attempts: 1, hasLog: true,
    packages: ['qt6_base-6.7.2-1-arm64.hpkg', 'qt6_base_devel-6.7.2-1-arm64.hpkg', 'qt6_base_tools-6.7.2-1-arm64.hpkg', 'qt6_base_debuginfo-6.7.2-1-arm64.hpkg', 'qt6_base_source-6.7.2-1-source.hpkg'] }),
]

const send = (res, status, body, type = 'application/json') => {
  res.writeHead(status, { 'Content-Type': type })
  res.end(body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body))
}
const egress = { monthlyCapGb: 800, month: new Date().toISOString().slice(0, 7), bytesServed: 312 * 1024 ** 3 }
const autoUpdate = { enabled: false, intervalHours: 6, platforms: ['arm64'], running: false, lastRunAt: null, lastCommit: null, lastResult: null }
/** The server's paged shape: pageSize defaults to 50, clamped to 1..200; page below 1 is 1. */
const paged = (url, all) => {
  const page = Math.max(parseInt(url.searchParams.get('page'), 10) || 1, 1)
  const pageSize = Math.min(Math.max(parseInt(url.searchParams.get('pageSize'), 10) || 50, 1), 200)
  return { items: all.slice((page - 1) * pageSize, page * pageSize), total: all.length, page, pageSize }
}
const problem = (res, status, title) => send(res, status, { status, title }, 'application/problem+json')
const readJson = (req) => new Promise((ok) => { let b = ''; req.on('data', (c) => (b += c)); req.on('end', () => { try { ok(JSON.parse(b || '{}')) } catch { ok({}) } }) })

http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x')
  const p = url.pathname
  const m = (re) => p.match(re)
  const admin = user?.roles.includes('Admin')
  let r
  console.log(req.method, req.url)

  if (p === '/api/version') return send(res, 200, { version: '0.0.0-mock', commit: null })
  if (p === '/api/platforms') return send(res, 200, PLATFORMS.map((pl) => {
    const ps = packages.filter((x) => x.platform === pl)
    return { platform: pl, packageCount: ps.length, totalBytes: ps.reduce((a, x) => a + x.size, 0), lastUploadAt: ps[0]?.uploadedAt ?? null }
  }))
  if ((r = m(/^\/api\/repo\/([^/]+)\/packages$/))) {
    const s = (url.searchParams.get('search') || '').toLowerCase()
    return send(res, 200, paged(url, packages.filter((x) => x.platform === r[1] && x.fileName.toLowerCase().includes(s)).map(({ platform, ...x }) => x)))
  }
  if ((r = m(/^\/api\/repo\/([^/]+)\/packages\/(.+)$/)) && req.method === 'DELETE') {
    if (!admin) return problem(res, user ? 403 : 401, user ? 'Forbidden' : 'Unauthorized')
    const i = packages.findIndex((x) => x.platform === r[1] && x.fileName === decodeURIComponent(r[2]))
    if (i < 0) return problem(res, 404, 'Not Found')
    packages.splice(i, 1)
    return send(res, 204)
  }
  if ((r = m(/^\/repo\/([^/]+)\/packages\/(.+)$/))) {
    return packages.some((x) => x.platform === r[1] && x.fileName === decodeURIComponent(r[2])) ? send(res, 200, 'mock hpkg', 'application/octet-stream') : send(res, 404)
  }
  if (p === '/api/auth/providers') return send(res, 200, ['GitHub'])
  if (p === '/api/auth/me') return user ? send(res, 200, me(user)) : problem(res, 401, 'Unauthorized')
  if (p === '/api/auth/registration') return send(res, 200, { enabled: true })
  if (p === '/api/auth/register' && req.method === 'POST') {
    const { email, password, displayName } = await readJson(req)
    if (!/^\S+@\S+$/.test(email || '')) return send(res, 400, { status: 400, title: 'One or more validation errors occurred.', errors: { email: ['Email is invalid.'] } }, 'application/problem+json')
    if ((password || '').length < 8) return send(res, 400, { status: 400, title: 'One or more validation errors occurred.', errors: { password: ['Passwords must be at least 8 characters.'] } }, 'application/problem+json')
    if (!findUser(email)) {
      const u = { id: 'u' + nextId++, email, password, displayName, roles: ['Builder'], emailConfirmed: false, twoFactorEnabled: false, disabled: false, recovery: [] }
      users.push(u)
      console.log(`  confirm link: ${SPA}/confirm-email?userId=${u.id}&token=tok-${u.id}`)
    }
    return send(res, 202, {})
  }
  if (p === '/api/auth/confirm-email' && req.method === 'POST') {
    const { userId, token } = await readJson(req)
    const u = users.find((x) => x.id === userId)
    if (!u || token !== `tok-${u.id}`) return problem(res, 400, 'Invalid or expired link')
    u.emailConfirmed = true
    return send(res, 204)
  }
  if (p === '/api/auth/resend-confirmation' && req.method === 'POST') {
    const u = findUser((await readJson(req)).email)
    if (u && !u.emailConfirmed) console.log(`  confirm link: ${SPA}/confirm-email?userId=${u.id}&token=tok-${u.id}`)
    return send(res, 202, {})
  }
  if (p === '/api/auth/forgot-password' && req.method === 'POST') {
    const u = findUser((await readJson(req)).email)
    if (u?.emailConfirmed && !u.disabled) console.log(`  reset link: ${SPA}/reset-password?email=${encodeURIComponent(u.email)}&token=reset-${u.id}`)
    return send(res, 202, {})
  }
  if (p === '/api/auth/reset-password' && req.method === 'POST') {
    const { email, token, password } = await readJson(req)
    const u = findUser(email)
    if (!u || token !== `reset-${u.id}`) return problem(res, 400, 'Invalid or expired link')
    if ((password || '').length < 8) return send(res, 400, { status: 400, title: 'One or more validation errors occurred.', errors: { password: ['Passwords must be at least 8 characters.'] } }, 'application/problem+json')
    u.password = password
    return send(res, 204)
  }
  if (p === '/api/auth/login' && req.method === 'POST') {
    const { email, password } = await readJson(req)
    if (!email || password === 'bad') return problem(res, 401, 'Unauthorized')
    if (password === 'slow') return send(res, 429, '') // to see the rate-limit message
    let u = findUser(email)
    if (u?.password !== undefined && u.password !== password) return problem(res, 401, 'Unauthorized')
    if (!u) users.push(u = { id: 'u' + nextId++, email, displayName: email.split('@')[0], roles: password === 'admin' ? ['Admin', 'Builder'] : ['Builder'], emailConfirmed: true, twoFactorEnabled: false, disabled: false, recovery: [] })
    if (!u.emailConfirmed) return problem(res, 403, 'Confirm your email address first')
    if (u.disabled) return problem(res, 403, 'Account disabled')
    if (u.twoFactorEnabled) { pending2fa = u; return send(res, 200, { twoFactorRequired: true }) }
    user = u
    return send(res, 200, me(u))
  }
  if (p === '/api/auth/login/2fa' && req.method === 'POST') {
    const { code, recoveryCode } = await readJson(req)
    const u = pending2fa
    const i = u ? u.recovery.indexOf(recoveryCode) : -1
    if (!u || !(code === '123456' || i >= 0)) return problem(res, 401, 'Invalid code')
    if (i >= 0) u.recovery.splice(i, 1)
    user = u
    pending2fa = null
    return send(res, 200, me(u))
  }
  if (p === '/api/auth/logout') { user = null; return send(res, 204) }
  if ((r = m(/^\/api\/auth\/external\/(.+)$/))) {
    user = { id: 'u2', email: 'octo@example.com', displayName: 'Octo', roles: ['Builder'] }
    res.writeHead(302, { Location: url.searchParams.get('returnUrl') || '/' })
    return res.end()
  }

  if (!user) return problem(res, 401, 'Unauthorized')
  // Codes for the signed-in user: 123456, or (where noted) an unused recovery code.
  const codeOk = async (recoveryOk) => {
    const { code } = await readJson(req)
    const i = recoveryOk ? user.recovery.indexOf(code) : -1
    if (i >= 0) user.recovery.splice(i, 1)
    return code === '123456' || i >= 0
  }
  if (p === '/api/auth/mfa') return send(res, 200, { enabled: user.twoFactorEnabled, recoveryCodesLeft: user.recovery.length })
  if (p === '/api/auth/mfa/setup' && req.method === 'POST') {
    if (user.twoFactorEnabled) return problem(res, 400, 'Turn off two-factor sign-in first')
    const key = 'JBSWY3DPEHPK3PXP'
    return send(res, 200, { sharedKey: key, otpauthUri: `otpauth://totp/RenkuOS%20Ports:${encodeURIComponent(user.email)}?secret=${key}&issuer=RenkuOS%20Ports&digits=6` })
  }
  if (p === '/api/auth/mfa/enable' && req.method === 'POST') {
    if (!(await codeOk(false))) return problem(res, 400, 'Invalid code')
    user.twoFactorEnabled = true
    return send(res, 200, { recoveryCodes: (user.recovery = newCodes()) })
  }
  if (p === '/api/auth/mfa/disable' && req.method === 'POST') {
    if (!(await codeOk(true))) return problem(res, 400, 'Invalid code')
    Object.assign(user, { twoFactorEnabled: false, recovery: [] })
    return send(res, 204)
  }
  if (p === '/api/auth/mfa/recovery-codes' && req.method === 'POST') {
    if (!(await codeOk(true))) return problem(res, 400, 'Invalid code')
    return send(res, 200, { recoveryCodes: (user.recovery = newCodes()) })
  }
  if (p === '/api/auth/password' && req.method === 'POST') {
    const { currentPassword, newPassword } = await readJson(req)
    if (user.password === undefined) return problem(res, 400, 'No local password; use Forgot password to set one')
    if (user.password !== currentPassword) return send(res, 400, { status: 400, title: 'One or more validation errors occurred.', errors: { password: ['Incorrect password.'] } }, 'application/problem+json')
    if ((newPassword || '').length < 8) return send(res, 400, { status: 400, title: 'One or more validation errors occurred.', errors: { password: ['Passwords must be at least 8 characters.'] } }, 'application/problem+json')
    user.password = newPassword
    return send(res, 204)
  }
  if (p === '/api/clients' && req.method === 'GET') {
    return send(res, 200, admin && url.searchParams.get('all') === 'true' ? clients : clients.filter((c) => c.owner === user.displayName))
  }
  if (p === '/api/clients' && req.method === 'POST') {
    const { name, platform } = await readJson(req)
    const c = { id: nextId++, name, platform, owner: user.displayName, enabled: true, createdAt: new Date().toISOString(), lastSeenAt: null }
    clients.push(c)
    return send(res, 200, { id: c.id, name, platform, secret: 'rk_' + sha(name + Date.now()).slice(0, 40) })
  }
  if ((r = m(/^\/api\/clients\/(\d+)$/)) && req.method === 'DELETE') {
    const i = clients.findIndex((c) => c.id === +r[1])
    if (i < 0) return problem(res, 404, 'Not found')
    clients.splice(i, 1)
    return send(res, 204)
  }

  if (!admin) return problem(res, 403, 'Forbidden')
  if (p === '/api/dashboard') return send(res, 200, {
    counts: PLATFORMS.map((pl) => {
      const n = (s) => jobs.filter((j) => j.platform === pl && j.status === s).length
      return { platform: pl, queued: n('Queued'), inProgress: n('InProgress'), succeeded: n('Succeeded'), failed: n('Failed') }
    }),
    recentFailures: jobs.filter((j) => j.status === 'Failed'),
    clients,
  })
  if (p === '/api/admin/users') {
    const s = (url.searchParams.get('search') || '').toLowerCase()
    return send(res, 200, paged(url, users.filter((u) => (u.email + ' ' + u.displayName).toLowerCase().includes(s)).map((u) => ({
      id: u.id, email: u.email, displayName: u.displayName, roles: u.roles, emailConfirmed: u.emailConfirmed,
      twoFactorEnabled: u.twoFactorEnabled, disabled: u.disabled, clientCount: clients.filter((c) => c.owner === u.displayName).length,
    }))))
  }
  if ((r = m(/^\/api\/admin\/users\/([^/]+)\/(admin|disable|enable)$/))) {
    const u = users.find((x) => x.id === decodeURIComponent(r[1]))
    if (!u) return problem(res, 404, 'Not found')
    if (r[2] === 'admin' && req.method === 'DELETE') {
      if (u === user) return problem(res, 409, 'You cannot remove your own admin role')
      if (users.filter((x) => x.roles.includes('Admin')).length <= 1) return problem(res, 409, 'Cannot remove the last admin')
      u.roles = u.roles.filter((x) => x !== 'Admin')
    } else if (r[2] === 'admin') { if (!u.roles.includes('Admin')) u.roles = ['Admin', ...u.roles] }
    else if (r[2] === 'disable') { if (u === user) return problem(res, 409, 'You cannot disable yourself'); u.disabled = true }
    else u.disabled = false
    return send(res, 204)
  }
  if (p === '/api/recipes/sync') return send(res, 200, { added: 0, updated: 2, total: recipes.length })
  if (p === '/api/settings/auto-update' && req.method === 'GET') return send(res, 200, autoUpdate)
  if (p === '/api/settings/auto-update' && req.method === 'PUT') {
    const { enabled, intervalHours, platforms } = await readJson(req)
    if (!(intervalHours >= 1)) return problem(res, 400, 'One or more validation errors occurred.')
    Object.assign(autoUpdate, { enabled: !!enabled, intervalHours, platforms: PLATFORMS.filter((x) => platforms?.includes(x)) })
    return send(res, 200, autoUpdate)
  }
  if (p === '/api/settings/egress' && req.method === 'GET') return send(res, 200, egress)
  if (p === '/api/settings/egress' && req.method === 'PUT') {
    const { monthlyCapGb } = await readJson(req)
    if (!(Number.isInteger(monthlyCapGb) && monthlyCapGb >= 1 && monthlyCapGb <= 1000000)) return problem(res, 400, 'One or more validation errors occurred.')
    egress.monthlyCapGb = monthlyCapGb
    return send(res, 200, egress)
  }
  if (p === '/api/settings/auto-update/run' && req.method === 'POST') {
    Object.assign(autoUpdate, { lastRunAt: new Date().toISOString(), lastCommit: '3f2a9c1d0b7e5a4c3f2a9c1d0b7e5a4c3f2a9c1d', lastResult: {
      changedPorts: ['curl', 'vim'], queued: 1, note: null, error: null,
      skipped: [{ port: 'vim', platform: 'arm64', reason: 'vim-9.1.0-1 already published; bump REVISION to rebuild' }] } })
    return send(res, 200, autoUpdate)
  }
  if (p === '/api/recipes') {
    const s = (url.searchParams.get('search') || '').toLowerCase()
    const rows = recipes.filter((x) => x.portName.includes(s)).map((x) => ({ ...x, priority: (x.id * 7) % 16, dependents: (x.id * 3) % 5, outstanding: x.id % 3 !== 0 }))
    if (url.searchParams.get('sort') === 'priority') rows.sort((a, b) => b.priority - a.priority)
    return send(res, 200, paged(url, rows))
  }
  if (p === '/api/priorities') return send(res, 200, { running: false, lastError: null, platforms: PLATFORMS.map((pl) => ({ platform: pl, computedAt: ago(30), outstanding: 12 })) })
  if (p === '/api/priorities/recompute' && req.method === 'POST') return send(res, 202, null)
  if (/^\/api\/priorities\/[^/]+\/queue-top$/.test(p) && req.method === 'POST') return send(res, 200, [])
  if (p === '/api/jobs' && req.method === 'GET') {
    const st = url.searchParams.get('status'), pl = url.searchParams.get('platform')
    return send(res, 200, paged(url, jobs.filter((j) => (!st || j.status === st) && (!pl || j.platform === pl)).sort((a, b) => b.id - a.id)))
  }
  if (p === '/api/jobs' && req.method === 'POST') {
    const { portName, version, platforms } = await readJson(req)
    const made = platforms.filter((pl) => !jobs.some((j) => j.portName === portName && j.platform === pl && ['Queued', 'InProgress'].includes(j.status)))
      .map((pl) => job(nextId++, portName, version, pl, 'Queued', { queuedAt: new Date().toISOString() }))
    jobs.push(...made)
    return send(res, 200, made)
  }
  if ((r = m(/^\/api\/jobs\/(\d+)\/(log|requeue|cancel)$/))) {
    const j = jobs.find((x) => x.id === +r[1])
    if (!j) return problem(res, 404, 'Not found')
    if (r[2] === 'log') return send(res, 200, `== ${j.portName}-${j.version} (${j.platform}) ==\n...\nchecking for suffix of object files... configure: error\n${j.failureSummary ?? 'build ok'}\n`, 'text/plain')
    j.status = r[2] === 'requeue' ? 'Queued' : 'Cancelled'
    return send(res, 200, j)
  }
  if (p === '/api/admin/cleanup/logs' && req.method === 'POST') {
    const { olderThanDays } = await readJson(req)
    if (!(olderThanDays >= 1)) return problem(res, 400, 'One or more validation errors occurred.')
    return send(res, 200, { count: 3, bytes: 3 * 48213 })
  }
  if (p === '/api/admin/cleanup/files' && req.method === 'POST') {
    const { orphans, superseded, staged } = await readJson(req)
    const t = (on, count, bytes) => (on ? { count, bytes } : null)
    const r = { orphans: t(orphans, 4, 9.5e6), superseded: t(superseded, 6, 4.2e7), staged: t(staged, 12, 1.1e8) }
    const all = Object.values(r).filter(Boolean)
    return send(res, 200, { ...r, total: { count: all.reduce((a, x) => a + x.count, 0), bytes: all.reduce((a, x) => a + x.bytes, 0) } })
  }
  problem(res, 404, 'Not found')
}).listen(process.env.PORT || 8080, () => console.log(`mock backend on http://localhost:${process.env.PORT || 8080}`))
