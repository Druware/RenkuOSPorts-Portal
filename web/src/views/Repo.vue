<script setup>
import { ref, computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, formatBytes, formatDate, PLATFORMS } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import CopyButton from '../components/CopyButton.vue'
import Pager from '../components/Pager.vue'

const route = useRoute()
const platform = computed(() => route.params.platform)
const known = computed(() => PLATFORMS.includes(platform.value))
const repoUrl = computed(() => `${location.origin}/repo/${platform.value}`)
const addRepo = computed(() => `pkgman add-repo ${repoUrl.value}`)
const indexed = ref(false) // the platform's repo.info exists, so pkgman can add the repository

async function checkIndex() {
  const p = platform.value
  indexed.value = false
  try {
    const res = await fetch(`/repo/${p}/repo.info`, { cache: 'no-store' })
    if (p === platform.value) indexed.value = res.ok
  } catch { /* offline: leave the hint hidden */ }
}

// Search and page live in the URL (?q=&page=), so reload, back and deep links work.
const router = useRouter()
const query = computed(() => (typeof route.query.q === 'string' ? route.query.q : ''))
const page = computed(() => Math.max(parseInt(route.query.page, 10) || 1, 1))
const search = ref(query.value)
const packages = ref([])
const total = ref(0)
const pageSize = ref(50)
const loading = ref(false)
const error = ref('')
let timer
let seq = 0

async function load() {
  if (!known.value) return
  const mine = ++seq // ignore responses that arrive after a newer request
  loading.value = true
  error.value = ''
  try {
    const q = new URLSearchParams({ page: String(page.value) })
    if (query.value.trim()) q.set('search', query.value.trim())
    const res = await api(`/repo/${platform.value}/packages?${q}`)
    if (mine === seq) { packages.value = res.items; total.value = res.total; pageSize.value = res.pageSize }
  } catch (e) {
    if (mine === seq) error.value = e.message
  } finally {
    if (mine === seq) loading.value = false
  }
}

const withQuery = (q, p) => ({ query: { ...(q ? { q } : {}), ...(p > 1 ? { page: String(p) } : {}) } })
const goToPage = (p) => router.push(withQuery(query.value, p))

watch(platform, () => { packages.value = []; total.value = 0; if (known.value) checkIndex() }, { immediate: true })
watch([platform, query, page], load, { immediate: true })
// A new search replaces the history entry and starts at page 1.
watch(search, (s) => {
  clearTimeout(timer)
  timer = setTimeout(() => { if (s.trim() !== query.value) router.replace(withQuery(s.trim(), 1)) }, 250)
})
watch(query, (q) => { if (q !== search.value.trim()) search.value = q })

const fileHref = (f) => `/repo/${platform.value}/packages/${encodeURIComponent(f)}`

/** What each verification state means, for the tooltip. */
function verificationTitle(p) {
  return {
    verified: 'Built independently by different build clients, with identical contents',
    'self-verified': 'Built twice with identical contents, both times by the same build client',
    override: 'The builds differed only in embedded dates; an administrator approved one',
  }[p.verification]
}
</script>

<template>
  <nav class="platform-switch" aria-label="Platforms">
    <RouterLink v-for="p in PLATFORMS" :key="p" :to="`/repo/${p}`">{{ p }}</RouterLink>
  </nav>

  <p v-if="!known" class="error" role="alert">Unknown platform “{{ platform }}”.</p>

  <HaikuWindow v-else :title="`Packages — ${platform}`">
    <p v-if="indexed" class="hint">
      Add this repository in Haiku:
      <code>{{ addRepo }}</code>
      <CopyButton :text="addRepo" label="Copy command" />
    </p>
    <p v-else class="hint muted">The pkgman repository index for {{ platform }} has not been built yet.</p>
    <div class="toolbar">
      <label for="pkg-search">Search packages</label>
      <input id="pkg-search" v-model="search" type="search" placeholder="e.g. gcc, python" autocomplete="off" />
      <span class="muted small" aria-live="polite">{{ loading ? 'Loading…' : '' }}</span>
    </div>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <div class="table-wrap">
      <table :class="{ loading }" :aria-busy="loading">
        <thead>
          <tr>
            <th scope="col">File</th>
            <th scope="col" class="num">Size</th>
            <th scope="col">SHA-256</th>
          </tr>
        </thead>
        <!-- One tbody per package: file, size and SHA on line 1; the details, indented, on line 2. -->
        <tbody v-for="p in packages" :key="p.fileName" class="pkg">
          <tr>
            <td class="col-file"><a :href="fileHref(p.fileName)" download>{{ p.fileName }}</a></td>
            <td class="num nowrap">{{ formatBytes(p.size) }}</td>
            <td class="nowrap"><code :title="p.sha256">{{ p.sha256?.slice(0, 12) }}…</code> <CopyButton :text="p.sha256" label="Copy" /></td>
          </tr>
          <tr class="details muted small">
            <td colspan="3">
              Built {{ formatDate(p.buildDate) }} · Uploaded {{ formatDate(p.uploadedAt) }} ·
              Recipe updated {{ formatDate(p.recipeUpdatedAt) }} · Built by {{ p.builtBy || '—' }}<template v-if="p.verification"> ·
              <span :title="verificationTitle(p)">{{ p.verification === 'verified' ? `verified: ${p.verifiedClients} clients` : p.verification }}</span></template>
            </td>
          </tr>
        </tbody>
        <tbody v-if="!loading && !packages.length && !error">
          <tr><td colspan="3" class="muted">{{ total ? 'No packages on this page.' : `No packages${query ? ' match your search' : ' yet'}.` }}</td></tr>
        </tbody>
      </table>
    </div>
    <Pager v-if="total" :page="page" :page-size="pageSize" :total="total" @update:page="goToPage" />
  </HaikuWindow>
</template>
