// Dev-only mock of the backend contract on :8080 so the SPA can be checked without .NET.
// Run: npm run mock (then npx vite). Not imported by the app, so it never reaches the bundle.
// Sign in with any email and password "admin" (Admin) or anything else (Builder); "bad" -> 401.
import http from 'node:http'

const PLATFORMS = ['x86', 'x86_64', 'arm64', 'riscv64']
const ago = (h) => new Date(Date.now() - h * 3600e3).toISOString()
const sha = (s) => [...Array(64)].map((_, i) => '0123456789abcdef'[(s.charCodeAt(i % s.length) * (i + 7)) % 16]).join('')

let user = null
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
  if (p === '/api/auth/me') return user ? send(res, 200, user) : problem(res, 401, 'Unauthorized')
  if (p === '/api/auth/login' && req.method === 'POST') {
    const { email, password } = await readJson(req)
    if (!email || password === 'bad') return problem(res, 401, 'Unauthorized')
    user = { id: 'u1', email, displayName: email.split('@')[0], roles: password === 'admin' ? ['Admin', 'Builder'] : ['Builder'] }
    return send(res, 200, user)
  }
  if (p === '/api/auth/logout') { user = null; return send(res, 204) }
  if ((r = m(/^\/api\/auth\/external\/(.+)$/))) {
    user = { id: 'u2', email: 'octo@example.com', displayName: 'Octo', roles: ['Builder'] }
    res.writeHead(302, { Location: url.searchParams.get('returnUrl') || '/' })
    return res.end()
  }

  if (!user) return problem(res, 401, 'Unauthorized')
  if (p === '/api/clients' && req.method === 'GET') return send(res, 200, clients)
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
  if (p === '/api/recipes/sync') return send(res, 200, { added: 0, updated: 2, total: recipes.length })
  if (p === '/api/settings/auto-update' && req.method === 'GET') return send(res, 200, autoUpdate)
  if (p === '/api/settings/auto-update' && req.method === 'PUT') {
    const { enabled, intervalHours, platforms } = await readJson(req)
    if (!(intervalHours >= 1)) return problem(res, 400, 'One or more validation errors occurred.')
    Object.assign(autoUpdate, { enabled: !!enabled, intervalHours, platforms: PLATFORMS.filter((x) => platforms?.includes(x)) })
    return send(res, 200, autoUpdate)
  }
  if (p === '/api/settings/auto-update/run' && req.method === 'POST') {
    Object.assign(autoUpdate, { lastRunAt: new Date().toISOString(), lastCommit: '3f2a9c1d0b7e5a4c3f2a9c1d0b7e5a4c3f2a9c1d', lastResult: {
      changedPorts: ['curl', 'vim'], queued: 1, note: null, error: null,
      skipped: [{ port: 'vim', platform: 'arm64', reason: 'vim-9.1.0-1 already published; bump REVISION to rebuild' }] } })
    return send(res, 200, autoUpdate)
  }
  if (p === '/api/recipes') {
    const s = (url.searchParams.get('search') || '').toLowerCase()
    return send(res, 200, paged(url, recipes.filter((x) => x.portName.includes(s))))
  }
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
  problem(res, 404, 'Not found')
}).listen(process.env.PORT || 8080, () => console.log(`mock backend on http://localhost:${process.env.PORT || 8080}`))
