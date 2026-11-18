<script setup>
import { ref, reactive, watch, onMounted, onUnmounted } from 'vue'
import { api, formatBytes, formatDate, PLATFORMS, STATUSES } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import JobLog from '../components/JobLog.vue'
import StatusBadge from '../components/StatusBadge.vue'
import CopyButton from '../components/CopyButton.vue'
import Pager from '../components/Pager.vue'

const tab = ref('dashboard')
const error = ref('')
const notice = ref('')

function fail(e) { error.value = e.message; notice.value = '' }
function ok(msg) { notice.value = msg; error.value = '' }

// ---- Dashboard (polled every 15 s while the tab and page are visible) ----
const dash = ref(null)
async function loadDashboard() {
  try { dash.value = await api('/dashboard') } catch (e) { fail(e) }
}
let poll
function tick() {
  if (tab.value === 'dashboard' && document.visibilityState === 'visible') loadDashboard()
}
document.addEventListener('visibilitychange', tick)
onMounted(() => { loadDashboard(); poll = setInterval(tick, 15000) })
onUnmounted(() => { clearInterval(poll); document.removeEventListener('visibilitychange', tick) })

// ---- Jobs ----
const filter = reactive({ status: '', platform: '' })
const jobs = ref({ items: [], total: 0, page: 1, pageSize: 50 })
const jobPage = ref(1)
const jobsLoading = ref(false)
async function loadJobs() {
  const q = new URLSearchParams({ page: String(jobPage.value) })
  if (filter.status) q.set('status', filter.status)
  if (filter.platform) q.set('platform', filter.platform)
  jobsLoading.value = true
  try { jobs.value = await api(`/jobs?${q}`) } catch (e) { fail(e) } finally { jobsLoading.value = false }
}
watch(filter, () => { jobPage.value = 1; loadJobs() })
function goToJobPage(p) { jobPage.value = p; loadJobs() }

async function jobAction(job, action) {
  try {
    await api(`/jobs/${job.id}/${action}`, { method: 'POST' })
    ok(`${job.portName} (${job.platform}) ${action === 'requeue' ? 'requeued' : 'cancelled'}.`)
    await Promise.all([loadDashboard(), tab.value === 'jobs' && loadJobs()])
  } catch (e) { fail(e) }
}

// ---- Recipes ----
const recipeSearch = ref('')
const recipes = ref({ items: [], total: 0, page: 1, pageSize: 50 })
const recipePage = ref(1)
const recipesLoading = ref(false)
const selected = ref(null) // kept across pages and searches
const targets = ref([...PLATFORMS])
const syncing = ref(false)
let recipeTimer
async function loadRecipes() {
  const q = new URLSearchParams({ page: String(recipePage.value) })
  if (recipeSearch.value.trim()) q.set('search', recipeSearch.value.trim())
  recipesLoading.value = true
  try { recipes.value = await api(`/recipes?${q}`) } catch (e) { fail(e) } finally { recipesLoading.value = false }
}
watch(recipeSearch, () => { clearTimeout(recipeTimer); recipeTimer = setTimeout(() => { recipePage.value = 1; loadRecipes() }, 250) })
function goToRecipePage(p) { recipePage.value = p; loadRecipes() }

// ---- Automatic updates (queue changed ports on a schedule) ----
const auto = ref(null)
const autoForm = reactive({ enabled: false, intervalHours: 6, platforms: [] })
const autoBusy = ref(false)
function showAuto(a) {
  auto.value = a
  Object.assign(autoForm, { enabled: a.enabled, intervalHours: a.intervalHours, platforms: [...a.platforms] })
}
async function loadAuto() {
  try { showAuto(await api('/settings/auto-update')) } catch (e) { fail(e) }
}
async function saveAuto() {
  try { showAuto(await api('/settings/auto-update', { method: 'PUT', body: autoForm })); ok('Automatic update settings saved.') } catch (e) { fail(e) }
}
async function runAuto() {
  autoBusy.value = true
  try {
    showAuto(await api('/settings/auto-update/run', { method: 'POST' }))
    const r = auto.value.lastResult
    if (r?.error) fail(new Error(`Automatic update failed: ${r.error}`))
    else ok(`Automatic update: ${r?.note || `${r?.changedPorts.length ?? 0} changed port(s), ${r?.queued ?? 0} job(s) queued.`}`)
    await loadDashboard()
  } catch (e) { fail(e) } finally { autoBusy.value = false }
}

async function sync() {
  syncing.value = true
  try {
    const r = await api('/recipes/sync', { method: 'POST' })
    ok(`Recipe sync: ${r.added} added, ${r.updated} updated, ${r.total} total.`)
    await loadRecipes()
  } catch (e) { fail(e) } finally { syncing.value = false }
}

async function queue() {
  const r = selected.value
  try {
    const created = await api('/jobs', { method: 'POST', body: { portName: r.portName, version: r.version, platforms: targets.value } })
    ok(`Queued ${created.length} job(s) for ${r.portName}-${r.version}` +
      (created.length < targets.value.length ? ' (platforms with a pending job were skipped).' : '.'))
  } catch (e) { fail(e) }
}

async function reindex(platform) {
  try {
    await api(`/repo/${platform}/reindex`, { method: 'POST' })
    ok(`Rebuilding the ${platform} repository index; the dashboard shows the result when it is done.`)
  } catch (e) { fail(e) }
}

// ---- Import from another pkgman repository (polled every 3 s while one is running) ----
const importForm = reactive({ sourceUrl: '', platform: 'arm64' })
const imports = ref([])
const current = ref(null)
let importPoll
async function loadImports() {
  try { imports.value = await api('/imports') } catch (e) { fail(e) }
}
async function watchImport(id) {
  clearTimeout(importPoll)
  try { current.value = await api(`/imports/${id}`) } catch (e) { fail(e); return }
  if (current.value.status === 'Running') importPoll = setTimeout(() => watchImport(id), 3000)
  else loadImports()
}
async function startImport(dryRun) {
  try {
    const { id } = await api('/imports', { method: 'POST', body: { ...importForm, dryRun } })
    ok(`${dryRun ? 'Dry run' : 'Import'} started.`)
    await Promise.all([watchImport(id), loadImports()])
  } catch (e) { fail(e) }
}
async function cancelImport(id) {
  try { await api(`/imports/${id}/cancel`, { method: 'POST' }); ok('Cancelling after the files in progress.') } catch (e) { fail(e) }
}
onUnmounted(() => clearTimeout(importPoll))

// ---- Packages (browse and delete published packages) ----
const pkg = reactive({ platform: 'arm64', search: '', page: 1 })
const pkgs = ref({ items: [], total: 0, page: 1, pageSize: 50 })
const pkgLoading = ref(false)
const pkgError = ref('')
let pkgTimer
let pkgSeq = 0
async function loadPackages() {
  const mine = ++pkgSeq // ignore responses that arrive after a newer request
  pkgLoading.value = true
  try {
    const q = new URLSearchParams({ page: String(pkg.page) })
    if (pkg.search.trim()) q.set('search', pkg.search.trim())
    const res = await api(`/repo/${pkg.platform}/packages?${q}`)
    if (mine === pkgSeq) { pkgs.value = res; pkgError.value = '' }
  } catch (e) {
    if (mine === pkgSeq) pkgError.value = e.message
  } finally {
    if (mine === pkgSeq) pkgLoading.value = false
  }
}
watch(() => pkg.platform, () => { pkg.page = 1; loadPackages() })
watch(() => pkg.search, () => { clearTimeout(pkgTimer); pkgTimer = setTimeout(() => { pkg.page = 1; loadPackages() }, 250) })
function goToPackagePage(p) { pkg.page = p; loadPackages() }

async function deletePackage(p) {
  if (!confirm(`Delete ${p.fileName} (${pkg.platform})?\n\nThe file is removed from the repository and from the index on its next rebuild. This cannot be undone.`)) return
  try {
    await api(`/repo/${pkg.platform}/packages/${encodeURIComponent(p.fileName)}`, { method: 'DELETE' })
    pkgs.value.items = pkgs.value.items.filter((x) => x !== p)
    pkgs.value.total--
    pkgError.value = ''
    ok(`Deleted ${p.fileName} (${pkg.platform}).`)
    if (!pkgs.value.items.length && pkgs.value.total) goToPackagePage(Math.max(1, Math.min(pkg.page, Math.ceil(pkgs.value.total / pkgs.value.pageSize))))
  } catch (e) {
    pkgError.value = `Deleting ${p.fileName} failed: ${e.message}`
  }
}

watch(tab, (t) => {
  notice.value = ''
  if (t === 'dashboard') loadDashboard()
  if (t === 'import') loadImports()
  if (t === 'jobs') loadJobs()
  if (t === 'packages') loadPackages()
  if (t === 'recipes') { if (!recipes.value.items.length) loadRecipes(); loadAuto() }
})
</script>

<template>
  <h1 class="page-title">Ports dashboard</h1>
  <div class="tabs" role="group" aria-label="Admin sections">
    <button v-for="[k, label] in [['dashboard', 'Dashboard'], ['jobs', 'Jobs'], ['recipes', 'Recipes'], ['packages', 'Packages'], ['import', 'Import']]" :key="k" type="button"
      class="tab" :aria-pressed="tab === k" @click="tab = k">{{ label }}</button>
  </div>

  <p v-if="error" class="error" role="alert">{{ error }}</p>
  <p v-if="notice" class="notice" role="status">{{ notice }}</p>

  <template v-if="tab === 'dashboard'">
    <HaikuWindow title="Build status">
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col">Platform</th><th scope="col" class="num">Queued</th><th scope="col" class="num">In progress</th><th scope="col" class="num">Succeeded</th><th scope="col" class="num">Failed</th></tr></thead>
          <tbody>
            <tr v-for="c in dash?.counts || []" :key="c.platform">
              <th scope="row">{{ c.platform }}</th>
              <td class="num">{{ c.queued }}</td><td class="num">{{ c.inProgress }}</td><td class="num">{{ c.succeeded }}</td>
              <td class="num" :class="{ 'text-danger': c.failed }">{{ c.failed }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="muted small">Refreshes every 15 seconds.</p>
    </HaikuWindow>

    <HaikuWindow title="Recent failures">
      <p v-if="!dash?.recentFailures?.length" class="muted">No recent failures.</p>
      <ul class="failures">
        <li v-for="j in dash?.recentFailures || []" :key="j.id">
          <div class="failure-head">
            <strong>{{ j.portName }}-{{ j.version }}</strong>
            <span class="pill">{{ j.platform }}</span>
            <span class="muted small">{{ formatDate(j.completedAt) }} · attempt {{ j.attempts }}</span>
            <button type="button" class="btn btn-small" @click="jobAction(j, 'requeue')">Requeue</button>
          </div>
          <p v-if="j.failureSummary" class="summary">{{ j.failureSummary }}</p>
          <JobLog v-if="j.hasLog" :job-id="j.id" />
        </li>
      </ul>
    </HaikuWindow>

    <HaikuWindow title="Repository index">
      <ul class="rows">
        <li v-for="r in dash?.repoIndex || []" :key="r.platform">
          <div class="row-head">
            <span class="pill">{{ r.platform }}</span>
            <span>{{ r.packageCount }} package{{ r.packageCount === 1 ? '' : 's' }}</span>
            <span class="row-actions"><button type="button" class="btn btn-small" :aria-label="`Reindex ${r.platform}`" @click="reindex(r.platform)">Reindex</button></span>
          </div>
          <div class="row-details muted small">
            Built {{ formatDate(r.builtAt) }}
            <div v-if="r.error" class="text-danger">{{ r.error }}</div>
            <details v-if="r.excluded.length">
              <summary>{{ r.excluded.length }} excluded</summary>
              <ul><li v-for="x in r.excluded" :key="x.fileName"><code>{{ x.fileName }}</code>: {{ x.reason }}</li></ul>
            </details>
          </div>
        </li>
      </ul>
    </HaikuWindow>

    <HaikuWindow title="Build clients">
      <ul class="rows">
        <li v-for="c in dash?.clients || []" :key="c.id">
          <div class="row-head">
            <strong>{{ c.name }}</strong>
            <span class="pill">{{ c.platform }}</span>
            <span v-if="!c.enabled" class="badge badge-Cancelled">Disabled</span>
          </div>
          <div class="row-details muted small">Owner {{ c.owner }} · Last seen {{ formatDate(c.lastSeenAt) }}</div>
        </li>
        <li v-if="!dash?.clients?.length" class="muted">No build clients registered.</li>
      </ul>
    </HaikuWindow>
  </template>

  <HaikuWindow v-else-if="tab === 'jobs'" title="Jobs">
    <div class="toolbar">
      <label for="jf-status">Status</label>
      <select id="jf-status" v-model="filter.status">
        <option value="">All</option>
        <option v-for="s in STATUSES" :key="s" :value="s">{{ s }}</option>
      </select>
      <label for="jf-platform">Platform</label>
      <select id="jf-platform" v-model="filter.platform">
        <option value="">All</option>
        <option v-for="p in PLATFORMS" :key="p" :value="p">{{ p }}</option>
      </select>
      <button type="button" class="btn btn-small" @click="loadJobs">Refresh</button>
    </div>
    <!-- Two-line rows: port, platform, status and actions on line 1 (actions wrap under the port when narrow); the details, indented, on line 2. -->
    <ul class="rows" :class="{ loading: jobsLoading }" :aria-busy="jobsLoading">
      <li v-for="j in jobs.items" :key="j.id">
        <div class="row-head">
          <strong>{{ j.portName }}-{{ j.version }}</strong>
          <span class="pill">{{ j.platform }}</span>
          <StatusBadge :status="j.status" />
          <span class="row-actions">
            <button v-if="j.status === 'Queued'" type="button" class="btn btn-small btn-danger" :aria-label="`Cancel ${j.portName}-${j.version} (${j.platform})`" @click="jobAction(j, 'cancel')">Cancel</button>
            <button v-if="j.status === 'Failed' || j.status === 'Cancelled'" type="button" class="btn btn-small" :aria-label="`Requeue ${j.portName}-${j.version} (${j.platform})`" @click="jobAction(j, 'requeue')">Requeue</button>
          </span>
        </div>
        <div class="row-details muted small">
          Queued {{ formatDate(j.queuedAt) }} by {{ j.queuedBy }} · Client {{ j.claimedBy || '—' }} ·
          Completed {{ formatDate(j.completedAt) }} · Attempts {{ j.attempts }}
          <p v-if="j.failureSummary" class="summary">{{ j.failureSummary }}</p>
          <div v-if="j.packages?.length > 3">
            <details><summary>{{ j.packages.length }} packages</summary><ul class="pkg-names"><li v-for="n in j.packages" :key="n"><code>{{ n }}</code></li></ul></details>
          </div>
          <div v-else-if="j.packages?.length" class="pkg-names-inline">Packages: <code v-for="n in j.packages" :key="n">{{ n }}</code></div>
          <JobLog v-if="j.hasLog" :job-id="j.id" />
        </div>
      </li>
      <li v-if="!jobsLoading && !jobs.items.length" class="muted">{{ jobs.total ? 'No jobs on this page.' : 'No jobs match.' }}</li>
    </ul>
    <Pager v-if="jobs.total" :page="jobs.page" :page-size="jobs.pageSize" :total="jobs.total" noun="job" @update:page="goToJobPage" />
  </HaikuWindow>

  <HaikuWindow v-else-if="tab === 'packages'" title="Packages">
    <div class="toolbar">
      <label for="pk-platform">Platform</label>
      <select id="pk-platform" v-model="pkg.platform">
        <option v-for="p in PLATFORMS" :key="p" :value="p">{{ p }}</option>
      </select>
      <label for="pk-search">Search packages</label>
      <input id="pk-search" v-model="pkg.search" type="search" autocomplete="off" />
      <span class="muted small" aria-live="polite">{{ pkgLoading ? 'Loading…' : '' }}</span>
    </div>
    <p v-if="pkgError" class="error" role="alert">{{ pkgError }}</p>
    <!-- Two-line rows like the repository page: file, size and SHA on line 1 with Delete at the right; details, indented, on line 2. -->
    <ul class="rows" :class="{ loading: pkgLoading }" :aria-busy="pkgLoading">
      <li v-for="p in pkgs.items" :key="p.fileName">
        <div class="row-head">
          <span class="row-file">{{ p.fileName }}</span>
          <span class="nowrap">{{ formatBytes(p.size) }}</span>
          <span class="nowrap"><code :title="p.sha256">{{ p.sha256?.slice(0, 12) }}…</code> <CopyButton :text="p.sha256" label="Copy" /></span>
          <span class="row-actions"><button type="button" class="btn btn-small btn-danger" :aria-label="`Delete ${p.fileName}`" @click="deletePackage(p)">Delete</button></span>
        </div>
        <div class="row-details muted small">
          Built {{ formatDate(p.buildDate) }} · Uploaded {{ formatDate(p.uploadedAt) }} ·
          Recipe updated {{ formatDate(p.recipeUpdatedAt) }} · Built by {{ p.builtBy || '—' }}
        </div>
      </li>
      <li v-if="!pkgLoading && !pkgs.items.length && !pkgError" class="muted">{{ pkgs.total ? 'No packages on this page.' : `No packages${pkg.search ? ' match your search' : ''}.` }}</li>
    </ul>
    <Pager v-if="pkgs.total" :page="pkgs.page" :page-size="pkgs.pageSize" :total="pkgs.total" @update:page="goToPackagePage" />
  </HaikuWindow>

  <template v-else-if="tab === 'import'">
    <HaikuWindow title="Import from a repository">
      <p class="muted small">Copies the packages a pkgman repository has and this platform lacks (by file name). Each file is checked against the source's checksum and with Haiku's package tool; existing packages are never replaced.</p>
      <form class="form" @submit.prevent="startImport(false)">
        <label for="im-url">Repository base URL (as for pkgman add-repo)</label>
        <input id="im-url" v-model.trim="importForm.sourceUrl" type="url" required placeholder="https://example.org/repo/arm64" autocomplete="off" />
        <label for="im-platform">Platform</label>
        <select id="im-platform" v-model="importForm.platform">
          <option v-for="p in PLATFORMS" :key="p" :value="p">{{ p }}</option>
        </select>
        <div class="toolbar">
          <button type="button" class="btn" :disabled="!importForm.sourceUrl || current?.status === 'Running'" @click="startImport(true)">Dry run</button>
          <button type="submit" class="btn btn-primary" :disabled="!importForm.sourceUrl || current?.status === 'Running'">Import</button>
        </div>
      </form>
    </HaikuWindow>

    <HaikuWindow v-if="current" :title="`Import ${current.id}${current.dryRun ? ' (dry run)' : ''}`">
      <div class="failure-head">
        <StatusBadge :status="current.status" />
        <span class="pill">{{ current.platform }}</span>
        <span class="muted small">{{ current.sourceUrl }}</span>
        <button v-if="current.status === 'Running'" type="button" class="btn btn-small btn-danger" @click="cancelImport(current.id)">Cancel</button>
      </div>
      <p aria-live="polite">
        {{ current.total }} in the source · {{ current.missing }} missing here · {{ current.imported }} imported · {{ current.skipped.length }} skipped
        <span v-if="current.status === 'Running'" class="muted small">(refreshes every 3 seconds)</span>
      </p>
      <p v-if="current.error" class="summary text-danger">{{ current.error }}</p>
      <details v-if="current.skipped.length" :open="current.skipped.length <= 20">
        <summary>{{ current.dryRun ? 'Missing packages' : 'Skipped packages' }}</summary>
        <ul class="small"><li v-for="s in current.skipped" :key="s.fileName"><code>{{ s.fileName }}</code>: {{ s.reason }}</li></ul>
      </details>
    </HaikuWindow>

    <HaikuWindow title="Import history">
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col">Started</th><th scope="col">Source</th><th scope="col">Platform</th><th scope="col">Status</th><th scope="col" class="num">Missing</th><th scope="col" class="num">Imported</th><th scope="col" class="num">Skipped</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            <tr v-for="i in imports" :key="i.id">
              <td class="nowrap">{{ formatDate(i.startedAt) }}<div class="small muted">{{ i.startedBy }}</div></td>
              <td class="small">{{ i.sourceUrl }}<span v-if="i.dryRun" class="muted"> (dry run)</span></td>
              <td>{{ i.platform }}</td>
              <td><StatusBadge :status="i.status" /></td>
              <td class="num">{{ i.missing }}</td><td class="num">{{ i.imported }}</td><td class="num">{{ i.skipped.length }}</td>
              <td><button type="button" class="btn btn-small" @click="watchImport(i.id)">Details</button></td>
            </tr>
            <tr v-if="!imports.length"><td colspan="8" class="muted">No imports yet.</td></tr>
          </tbody>
        </table>
      </div>
    </HaikuWindow>
  </template>

  <template v-else>
    <HaikuWindow title="Queue builds">
      <form class="form" @submit.prevent="queue">
        <p>{{ selected ? `${selected.category}/${selected.portName}-${selected.version}` : 'Select a recipe below.' }}</p>
        <fieldset>
          <legend>Platforms</legend>
          <label v-for="p in PLATFORMS" :key="p" class="check"><input type="checkbox" :value="p" v-model="targets" /> {{ p }}</label>
        </fieldset>
        <button class="btn btn-primary" type="submit" :disabled="!selected || !targets.length">Queue builds</button>
      </form>
    </HaikuWindow>

    <HaikuWindow title="Automatic updates">
      <p class="muted small">Every interval the server updates the ports tree, diffs it from the last processed commit and queues the changed ports that are already published on a platform, when their version and REVISION are not published yet.</p>
      <form class="form" @submit.prevent="saveAuto">
        <label class="check"><input v-model="autoForm.enabled" type="checkbox" /> Enabled</label>
        <label for="au-interval">Interval (hours)</label>
        <input id="au-interval" v-model.number="autoForm.intervalHours" type="number" min="1" max="720" required class="input-narrow" />
        <fieldset>
          <legend>Queue for platforms</legend>
          <label v-for="p in PLATFORMS" :key="p" class="check"><input type="checkbox" :value="p" v-model="autoForm.platforms" /> {{ p }}</label>
        </fieldset>
        <div class="toolbar">
          <button class="btn btn-primary" type="submit">Save</button>
          <button type="button" class="btn" :disabled="autoBusy || auto?.running" @click="runAuto">{{ autoBusy || auto?.running ? 'Running…' : 'Run now' }}</button>
        </div>
      </form>
      <div v-if="auto" class="small" aria-live="polite">
        <p>
          Last run {{ formatDate(auto.lastRunAt) }} · Last processed commit <code>{{ auto.lastCommit?.slice(0, 12) || '—' }}</code>
        </p>
        <template v-if="auto.lastResult">
          <p v-if="auto.lastResult.error" class="summary text-danger">{{ auto.lastResult.error }}</p>
          <p v-else-if="auto.lastResult.note" class="muted">{{ auto.lastResult.note }}</p>
          <p v-else>
            {{ auto.lastResult.changedPorts.length }} changed port(s), {{ auto.lastResult.queued }} job(s) queued, {{ auto.lastResult.skipped.length }} skipped.
            <span v-if="auto.lastResult.changedPorts.length" class="muted">Changed: {{ auto.lastResult.changedPorts.join(', ') }}</span>
          </p>
          <details v-if="auto.lastResult.skipped.length">
            <summary>Skipped</summary>
            <ul><li v-for="x in auto.lastResult.skipped" :key="x.port + x.platform"><code>{{ x.port }}</code> ({{ x.platform }}): {{ x.reason }}</li></ul>
          </details>
        </template>
      </div>
    </HaikuWindow>

    <HaikuWindow title="Recipes">
      <div class="toolbar">
        <label for="rc-search">Search recipes</label>
        <input id="rc-search" v-model="recipeSearch" type="search" autocomplete="off" />
        <button type="button" class="btn btn-small" :disabled="syncing" @click="sync">{{ syncing ? 'Syncing…' : 'Sync ports tree' }}</button>
      </div>
      <div class="table-wrap">
        <table :class="{ loading: recipesLoading }" :aria-busy="recipesLoading">
          <thead><tr><th scope="col"><span class="sr-only">Select</span></th><th scope="col">Port</th><th scope="col">Version</th><th scope="col">Category</th><th scope="col">Recipe updated</th></tr></thead>
          <tbody>
            <tr v-for="r in recipes.items" :key="r.id" :class="{ selected: selected?.id === r.id }">
              <td><input type="radio" name="recipe" :id="`rc-${r.id}`" :checked="selected?.id === r.id" @change="selected = r" /></td>
              <td><label :for="`rc-${r.id}`">{{ r.portName }}</label></td>
              <td>{{ r.version }}</td><td>{{ r.category }}</td><td class="nowrap">{{ formatDate(r.recipeUpdatedAt) }}</td>
            </tr>
            <tr v-if="!recipesLoading && !recipes.items.length"><td colspan="5" class="muted">{{ recipes.total ? 'No recipes on this page.' : 'No recipes found. Try syncing the ports tree.' }}</td></tr>
          </tbody>
        </table>
      </div>
      <Pager v-if="recipes.total" :page="recipes.page" :page-size="recipes.pageSize" :total="recipes.total" noun="recipe" @update:page="goToRecipePage" />
    </HaikuWindow>
  </template>

</template>
