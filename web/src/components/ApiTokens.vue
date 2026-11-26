<script setup>
import { ref, onMounted } from 'vue'
import { api, formatDate } from '../api.js'
import HaikuWindow from './HaikuWindow.vue'
import CopyButton from './CopyButton.vue'

// The signed-in admin's own issues-scoped API tokens (/api/admin/tokens).
const tokens = ref([])
const error = ref('')
const name = ref('')
const days = ref(30)
const busy = ref(false)
const created = ref(null) // { token, info } — the token is only ever shown here, once

async function load() {
  try { tokens.value = await api('/admin/tokens'); error.value = '' } catch (e) { error.value = e.message }
}

async function create() {
  busy.value = true
  error.value = ''
  try {
    created.value = await api('/admin/tokens', { method: 'POST', body: { name: name.value.trim(), expiresInDays: Number(days.value) } })
    name.value = ''
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}

async function revoke(t) {
  if (!confirm(`Revoke API token “${t.name}”? Anything using it stops working.`)) return
  try {
    await api(`/admin/tokens/${t.id}`, { method: 'DELETE' })
    if (created.value?.info.id === t.id) created.value = null
    await load()
  } catch (e) {
    error.value = e.message
  }
}

onMounted(load)
</script>

<template>
  <HaikuWindow title="API tokens">
    <p class="muted small">Your personal tokens for the build-issue endpoints (scope <code>issues</code>), sent as <code>Authorization: Bearer rpat_…</code>. They act as you and stop working if you lose the admin role.</p>
    <form class="form form-inline" @submit.prevent="create">
      <div>
        <label for="tok-name">Name</label>
        <input id="tok-name" v-model="name" required maxlength="100" placeholder="e.g. issue-triage-agent" />
      </div>
      <div>
        <label for="tok-days">Expires in (days)</label>
        <input id="tok-days" v-model="days" type="number" min="1" max="90" required />
      </div>
      <button class="btn btn-primary" type="submit" :disabled="busy || !name.trim()">Create token</button>
    </form>

    <div v-if="created" class="secret" role="status">
      <p><strong>Token “{{ created.info.name }}” created.</strong> Copy it now — you won't see it again.</p>
      <p class="secret-row"><span>Token</span> <code>{{ created.token }}</code> <CopyButton :text="created.token" label="Copy token" /></p>
      <p><button type="button" class="btn btn-small" @click="created = null">Dismiss</button></p>
    </div>

    <p v-if="error" class="error" role="alert">{{ error }}</p>

    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th scope="col">Name</th><th scope="col">Token</th><th scope="col">Scope</th><th scope="col">Created</th><th scope="col">Expires</th>
            <th scope="col">Last used</th><th scope="col">Revoked</th><th scope="col"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="t in tokens" :key="t.id">
            <td>{{ t.name }}</td><td><code>rpat_{{ t.prefix }}…</code></td><td>{{ t.scope }}</td>
            <td class="nowrap">{{ formatDate(t.createdAt) }}</td><td class="nowrap">{{ formatDate(t.expiresAt) }}</td>
            <td class="nowrap">{{ formatDate(t.lastUsedAt) }}</td><td class="nowrap">{{ formatDate(t.revokedAt) }}</td>
            <td><button v-if="!t.revokedAt" type="button" class="btn btn-small btn-danger" @click="revoke(t)" :aria-label="`Revoke ${t.name}`">Revoke</button></td>
          </tr>
          <tr v-if="!tokens.length"><td colspan="8" class="muted">No API tokens yet.</td></tr>
        </tbody>
      </table>
    </div>
  </HaikuWindow>
</template>
