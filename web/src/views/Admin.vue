<script setup>
import { ref, reactive, watch, onMounted, onUnmounted } from 'vue'
import { api, formatDate, PLATFORMS, STATUSES } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import JobLog from '../components/JobLog.vue'
import StatusBadge from '../components/StatusBadge.vue'

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
const jobs = ref([])
async function loadJobs() {
  const q = new URLSearchParams({ take: '100' })
  if (filter.status) q.set('status', filter.status)
  if (filter.platform) q.set('platform', filter.platform)
  try { jobs.value = await api(`/jobs?${q}`) } catch (e) { fail(e) }
}
watch(filter, loadJobs)

async function jobAction(job, action) {
  try {
    await api(`/jobs/${job.id}/${action}`, { method: 'POST' })
    ok(`${job.portName} (${job.platform}) ${action === 'requeue' ? 'requeued' : 'cancelled'}.`)
    await Promise.all([loadDashboard(), tab.value === 'jobs' && loadJobs()])
  } catch (e) { fail(e) }
}

// ---- Recipes ----
const recipeSearch = ref('')
const recipes = ref([])
const selected = ref(null)
const targets = ref([...PLATFORMS])
const syncing = ref(false)
let recipeTimer
async function loadRecipes() {
  const q = new URLSearchParams({ take: '100' })
  if (recipeSearch.value.trim()) q.set('search', recipeSearch.value.trim())
  try { recipes.value = await api(`/recipes?${q}`) } catch (e) { fail(e) }
}
watch(recipeSearch, () => { clearTimeout(recipeTimer); recipeTimer = setTimeout(loadRecipes, 250) })

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

watch(tab, (t) => {
  notice.value = ''
  if (t === 'dashboard') loadDashboard()
  if (t === 'jobs') loadJobs()
  if (t === 'recipes' && !recipes.value.length) loadRecipes()
})
</script>

<template>
  <h1 class="page-title">Ports dashboard</h1>
  <div class="tabs" role="group" aria-label="Admin sections">
    <button v-for="[k, label] in [['dashboard', 'Dashboard'], ['jobs', 'Jobs'], ['recipes', 'Recipes']]" :key="k" type="button"
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
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col">Platform</th><th scope="col">Built</th><th scope="col" class="num">Packages</th><th scope="col">Excluded</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>
            <tr v-for="r in dash?.repoIndex || []" :key="r.platform">
              <th scope="row">{{ r.platform }}</th>
              <td><span class="nowrap">{{ formatDate(r.builtAt) }}</span><div v-if="r.error" class="small text-danger">{{ r.error }}</div></td>
              <td class="num">{{ r.packageCount }}</td>
              <td class="small">
                <span v-if="!r.excluded.length" class="muted">—</span>
                <details v-else>
                  <summary>{{ r.excluded.length }} excluded</summary>
                  <ul><li v-for="x in r.excluded" :key="x.fileName"><code>{{ x.fileName }}</code>: {{ x.reason }}</li></ul>
                </details>
              </td>
              <td><button type="button" class="btn btn-small" @click="reindex(r.platform)">Reindex</button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </HaikuWindow>

    <HaikuWindow title="Build clients">
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col">Name</th><th scope="col">Platform</th><th scope="col">Owner</th><th scope="col">Enabled</th><th scope="col">Last seen</th></tr></thead>
          <tbody>
            <tr v-for="c in dash?.clients || []" :key="c.id">
              <td>{{ c.name }}</td><td>{{ c.platform }}</td><td>{{ c.owner }}</td><td>{{ c.enabled ? 'Yes' : 'No' }}</td><td class="nowrap">{{ formatDate(c.lastSeenAt) }}</td>
            </tr>
            <tr v-if="!dash?.clients?.length"><td colspan="5" class="muted">No build clients registered.</td></tr>
          </tbody>
        </table>
      </div>
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
    <div class="table-wrap">
      <table>
        <thead>
          <tr><th scope="col">Port</th><th scope="col">Platform</th><th scope="col">Status</th><th scope="col">Queued</th><th scope="col">Client</th><th scope="col">Completed</th><th scope="col">Packages</th><th scope="col"><span class="sr-only">Actions</span></th></tr>
        </thead>
        <tbody>
          <tr v-for="j in jobs" :key="j.id">
            <td>{{ j.portName }}-{{ j.version }}<JobLog v-if="j.hasLog" :job-id="j.id" /></td>
            <td>{{ j.platform }}</td>
            <td><StatusBadge :status="j.status" /><div v-if="j.failureSummary" class="small muted">{{ j.failureSummary }}</div></td>
            <td class="nowrap">{{ formatDate(j.queuedAt) }}<div class="small muted">{{ j.queuedBy }}</div></td>
            <td>{{ j.claimedBy || '—' }}</td>
            <td class="nowrap">{{ formatDate(j.completedAt) }}</td>
            <td class="small">{{ j.packages?.join(', ') || '—' }}</td>
            <td class="nowrap">
              <button v-if="j.status === 'Queued'" type="button" class="btn btn-small btn-danger" @click="jobAction(j, 'cancel')">Cancel</button>
              <button v-if="j.status === 'Failed' || j.status === 'Cancelled'" type="button" class="btn btn-small" @click="jobAction(j, 'requeue')">Requeue</button>
            </td>
          </tr>
          <tr v-if="!jobs.length"><td colspan="8" class="muted">No jobs match.</td></tr>
        </tbody>
      </table>
    </div>
  </HaikuWindow>

  <template v-else>
    <HaikuWindow title="Recipes">
      <div class="toolbar">
        <label for="rc-search">Search recipes</label>
        <input id="rc-search" v-model="recipeSearch" type="search" autocomplete="off" />
        <button type="button" class="btn btn-small" :disabled="syncing" @click="sync">{{ syncing ? 'Syncing…' : 'Sync ports tree' }}</button>
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col"><span class="sr-only">Select</span></th><th scope="col">Port</th><th scope="col">Version</th><th scope="col">Category</th><th scope="col">Recipe updated</th></tr></thead>
          <tbody>
            <tr v-for="r in recipes" :key="r.id" :class="{ selected: selected?.id === r.id }">
              <td><input type="radio" name="recipe" :id="`rc-${r.id}`" :value="r" v-model="selected" /></td>
              <td><label :for="`rc-${r.id}`">{{ r.portName }}</label></td>
              <td>{{ r.version }}</td><td>{{ r.category }}</td><td class="nowrap">{{ formatDate(r.recipeUpdatedAt) }}</td>
            </tr>
            <tr v-if="!recipes.length"><td colspan="5" class="muted">No recipes found. Try syncing the ports tree.</td></tr>
          </tbody>
        </table>
      </div>
    </HaikuWindow>

    <HaikuWindow title="Queue builds">
      <form class="form" @submit.prevent="queue">
        <p>{{ selected ? `${selected.category}/${selected.portName}-${selected.version}` : 'Select a recipe above.' }}</p>
        <fieldset>
          <legend>Platforms</legend>
          <label v-for="p in PLATFORMS" :key="p" class="check"><input type="checkbox" :value="p" v-model="targets" /> {{ p }}</label>
        </fieldset>
        <button class="btn btn-primary" type="submit" :disabled="!selected || !targets.length">Queue builds</button>
      </form>
    </HaikuWindow>
  </template>
</template>
