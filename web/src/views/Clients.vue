<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { api, formatDate, isAdmin, PLATFORMS } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import CopyButton from '../components/CopyButton.vue'

const clients = ref([])
const error = ref('')
const name = ref('')
const platform = ref('x86_64')
const busy = ref(false)
const showAll = ref(false) // admins only: every user's clients, not just their own
const created = ref(null) // { id, name, platform, secret } — the secret is only ever shown here, once

const command = computed(() => created.value &&
  `renkuports-client --Server ${location.origin} --ClientId ${created.value.id} --Secret ${created.value.secret} --BuildCommand ${created.value.platform === 'arm64' ? 'client/scripts/build-stack-arm64.sh' : '<your-build-script>'}`)

async function load() {
  try { clients.value = await api(showAll.value ? '/clients?all=true' : '/clients') } catch (e) { error.value = e.message }
}

async function create() {
  busy.value = true
  error.value = ''
  try {
    created.value = await api('/clients', { method: 'POST', body: { name: name.value.trim(), platform: platform.value } })
    name.value = ''
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}

async function remove(c) {
  if (!confirm(`Delete build client “${c.name}”? It will no longer be able to sign in.`)) return
  try {
    await api(`/clients/${c.id}`, { method: 'DELETE' })
    if (created.value?.id === c.id) created.value = null
    await load()
  } catch (e) {
    error.value = e.message
  }
}

watch(showAll, load)
onMounted(load)
</script>

<template>
  <HaikuWindow title="New build client">
    <form class="form form-inline" @submit.prevent="create">
      <div>
        <label for="cl-name">Name</label>
        <input id="cl-name" v-model="name" required maxlength="100" placeholder="e.g. garage-rpi5" />
      </div>
      <div>
        <label for="cl-platform">Platform</label>
        <select id="cl-platform" v-model="platform">
          <option v-for="p in PLATFORMS" :key="p" :value="p">{{ p }}</option>
        </select>
      </div>
      <button class="btn btn-primary" type="submit" :disabled="busy || !name.trim()">Create client</button>
    </form>

    <div v-if="created" class="secret" role="status">
      <p><strong>Client “{{ created.name }}” created.</strong> Copy the secret now — it will not be shown again.</p>
      <p class="secret-row"><span>Client ID</span> <code>{{ created.id }}</code> <CopyButton :text="String(created.id)" label="Copy ID" /></p>
      <p class="secret-row"><span>Secret</span> <code>{{ created.secret }}</code> <CopyButton :text="created.secret" label="Copy secret" /></p>
      <p>Command line:</p>
      <pre class="cmd">{{ command }}</pre>
      <p><CopyButton :text="command" label="Copy command" /> <button type="button" class="btn btn-small" @click="created = null">Dismiss</button></p>
    </div>
  </HaikuWindow>

  <p v-if="error" class="error" role="alert">{{ error }}</p>

  <HaikuWindow title="Build clients">
    <p v-if="isAdmin()"><label class="check"><input v-model="showAll" type="checkbox" /> Show all users' clients</label></p>
    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th scope="col">Name</th><th scope="col">Platform</th><th v-if="showAll" scope="col">Owner</th><th scope="col">Enabled</th>
            <th scope="col">Created</th><th scope="col">Last seen</th><th scope="col"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="c in clients" :key="c.id">
            <td>{{ c.name }}</td><td>{{ c.platform }}</td><td v-if="showAll">{{ c.owner }}</td><td>{{ c.enabled ? 'Yes' : 'No' }}</td>
            <td class="nowrap">{{ formatDate(c.createdAt) }}</td><td class="nowrap">{{ formatDate(c.lastSeenAt) }}</td>
            <td><button type="button" class="btn btn-small btn-danger" @click="remove(c)" :aria-label="`Delete ${c.name}`">Delete</button></td>
          </tr>
          <tr v-if="!clients.length"><td :colspan="showAll ? 7 : 6" class="muted">No build clients yet.</td></tr>
        </tbody>
      </table>
    </div>
  </HaikuWindow>
</template>
