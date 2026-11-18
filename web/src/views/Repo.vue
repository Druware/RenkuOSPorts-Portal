<script setup>
import { ref, computed, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, formatBytes, formatDate, PLATFORMS } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import CopyButton from '../components/CopyButton.vue'

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

const search = ref('')
const packages = ref([])
const loading = ref(false)
const error = ref('')
let timer
let seq = 0

async function load() {
  if (!known.value) return
  const mine = ++seq // ignore responses that arrive after a newer search
  loading.value = true
  error.value = ''
  try {
    const q = search.value.trim() ? `?search=${encodeURIComponent(search.value.trim())}` : ''
    const rows = await api(`/repo/${platform.value}/packages${q}`)
    if (mine === seq) packages.value = rows
  } catch (e) {
    if (mine === seq) error.value = e.message
  } finally {
    if (mine === seq) loading.value = false
  }
}

watch(platform, () => { search.value = ''; packages.value = []; load(); if (known.value) checkIndex() }, { immediate: true })
watch(search, () => { clearTimeout(timer); timer = setTimeout(load, 250) })

const fileHref = (f) => `/repo/${platform.value}/packages/${encodeURIComponent(f)}`
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
      <span class="muted small" aria-live="polite">{{ loading ? 'Loading…' : `${packages.length} package${packages.length === 1 ? '' : 's'}` }}</span>
    </div>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <div class="table-wrap">
      <table>
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
              Recipe updated {{ formatDate(p.recipeUpdatedAt) }} · Built by {{ p.builtBy || '—' }}
            </td>
          </tr>
        </tbody>
        <tbody v-if="!loading && !packages.length && !error">
          <tr><td colspan="3" class="muted">No packages{{ search ? ' match your search' : ' yet' }}.</td></tr>
        </tbody>
      </table>
    </div>
  </HaikuWindow>
</template>
