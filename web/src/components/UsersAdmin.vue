<script setup>
import { ref, reactive, watch, onMounted } from 'vue'
import { api } from '../api.js'
import HaikuWindow from './HaikuWindow.vue'
import Pager from './Pager.vue'

const q = reactive({ search: '', page: 1 })
const users = ref({ items: [], total: 0, page: 1, pageSize: 50 })
const loading = ref(false)
const error = ref('')
const notice = ref('')
let timer
let seq = 0
async function load() {
  const mine = ++seq // ignore responses that arrive after a newer request
  loading.value = true
  try {
    const params = new URLSearchParams({ page: String(q.page) })
    if (q.search.trim()) params.set('search', q.search.trim())
    const res = await api(`/admin/users?${params}`)
    if (mine === seq) { users.value = res; error.value = '' }
  } catch (e) {
    if (mine === seq) error.value = e.message
  } finally {
    if (mine === seq) loading.value = false
  }
}
watch(() => q.search, () => { clearTimeout(timer); timer = setTimeout(() => { q.page = 1; load() }, 250) })
function goToPage(p) { q.page = p; load() }

const isAdminUser = (u) => u.roles.includes('Admin')

/** Runs one admin action on a user; `prompt` (if any) must be confirmed first. */
async function act(u, method, action, done, prompt) {
  if (prompt && !confirm(prompt)) return
  notice.value = ''
  try {
    await api(`/admin/users/${encodeURIComponent(u.id)}/${action}`, { method })
    error.value = ''
    notice.value = done
    await load()
  } catch (e) {
    error.value = e.message // 409s (yourself, last admin) carry the server's reason
  }
}

onMounted(load)
</script>

<template>
  <HaikuWindow title="Users">
    <div class="toolbar">
      <label for="us-search">Search users</label>
      <input id="us-search" v-model="q.search" type="search" autocomplete="off" placeholder="Email or name" />
      <span class="muted small" aria-live="polite">{{ loading ? 'Loading…' : '' }}</span>
    </div>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="notice" class="notice" role="status">{{ notice }}</p>
    <div class="table-wrap">
      <table :class="{ loading }" :aria-busy="loading">
        <thead>
          <tr>
            <th scope="col">Email</th><th scope="col">Name</th><th scope="col">Roles</th><th scope="col">Confirmed</th>
            <th scope="col">MFA</th><th scope="col">Disabled</th><th scope="col" class="num">Clients</th><th scope="col"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="u in users.items" :key="u.id">
            <td>{{ u.email }}</td><td>{{ u.displayName }}</td><td>{{ u.roles.join(', ') || '—' }}</td>
            <td>{{ u.emailConfirmed ? 'Yes' : 'No' }}</td><td>{{ u.twoFactorEnabled ? 'On' : 'Off' }}</td><td>{{ u.disabled ? 'Yes' : 'No' }}</td>
            <td class="num">{{ u.clientCount }}</td>
            <td class="nowrap">
              <button v-if="isAdminUser(u)" type="button" class="btn btn-small" :aria-label="`Remove admin from ${u.email}`"
                @click="act(u, 'DELETE', 'admin', `${u.email} is no longer an admin.`, `Remove admin rights from ${u.email}?`)">Remove admin</button>
              <button v-else type="button" class="btn btn-small" :aria-label="`Make ${u.email} an admin`"
                @click="act(u, 'POST', 'admin', `${u.email} is now an admin.`)">Make admin</button>
              {{ ' ' }}
              <button v-if="u.disabled" type="button" class="btn btn-small" :aria-label="`Enable ${u.email}`"
                @click="act(u, 'POST', 'enable', `Enabled ${u.email}.`)">Enable</button>
              <button v-else type="button" class="btn btn-small btn-danger" :aria-label="`Disable ${u.email}`"
                @click="act(u, 'POST', 'disable', `Disabled ${u.email}.`, `Disable ${u.email}?\n\nThey are signed out everywhere and their build clients stop working until re-enabled.`)">Disable</button>
            </td>
          </tr>
          <tr v-if="!loading && !users.items.length && !error"><td colspan="8" class="muted">No users{{ q.search ? ' match your search' : '' }}.</td></tr>
        </tbody>
      </table>
    </div>
    <Pager v-if="users.total" :page="users.page" :page-size="users.pageSize" :total="users.total" noun="user" @update:page="goToPage" />
  </HaikuWindow>
</template>
