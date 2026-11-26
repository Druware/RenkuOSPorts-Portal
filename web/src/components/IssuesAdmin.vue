<script setup>
import { ref, reactive, onMounted } from 'vue'
import { api, formatDate } from '../api.js'
import HaikuWindow from './HaikuWindow.vue'
import JobLog from './JobLog.vue'

// Open and escalated build issues; a port with a failed job under one is not queued again on its platform until it is resolved.
const issues = ref([])
const loading = ref(false)
const error = ref('')
const notice = ref('')
const forms = reactive({}) // issue id -> { note, requeue }
const form = (i) => (forms[i.id] ||= { note: '', requeue: true })

async function load() {
  loading.value = true
  try { issues.value = await api('/issues'); error.value = '' } catch (e) { error.value = e.message } finally { loading.value = false }
}

async function act(i, action) {
  const f = form(i)
  notice.value = ''
  try {
    const r = await api(`/issues/${i.id}/${action}`, { method: 'POST', body: action === 'resolve' ? { note: f.note, requeue: f.requeue } : { note: f.note } })
    notice.value = action === 'resolve' ? `Issue #${i.id} resolved; ${r.requeued.length} job(s) requeued.` : `Issue #${i.id} escalated; admins emailed.`
    delete forms[i.id]
    await load()
  } catch (e) { error.value = e.message }
}

onMounted(load)
</script>

<template>
  <HaikuWindow title="Build issues">
    <div class="toolbar">
      <button type="button" class="btn btn-small" @click="load">Refresh</button>
      <span class="muted small" aria-live="polite">{{ loading ? 'Loading…' : '' }}</span>
    </div>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="notice" class="notice" role="status">{{ notice }}</p>
    <ul class="rows" :class="{ loading }" :aria-busy="loading">
      <li v-for="i in issues" :key="i.id">
        <div class="row-head">
          <strong>#{{ i.id }}</strong>
          <span class="pill">{{ i.platform }}</span>
          <span class="pill">{{ i.kind }}</span>
          <span :class="['badge', i.status === 'Escalated' ? 'badge-Failed' : 'badge-Queued']">{{ i.status }}</span>
          <span class="muted small">{{ i.count }} failure(s) · last {{ formatDate(i.lastSeenAt) }}</span>
        </div>
        <div class="row-details muted small">
          <p class="summary">{{ i.title }}</p>
          <p>Blocked: <span v-for="p in i.ports" :key="p.portName"><code>{{ p.portName }}</code> (job {{ p.latestJobId }}) </span><span v-if="!i.ports.length">—</span></p>
          <p v-if="i.notes" class="summary">{{ i.notes }}</p>
          <JobLog v-if="i.exampleJobId" :job-id="i.exampleJobId" />
          <form class="form" @submit.prevent="act(i, 'resolve')">
            <label :for="`is-note-${i.id}`">Note (required)</label>
            <input :id="`is-note-${i.id}`" v-model.trim="form(i).note" type="text" required maxlength="1000" autocomplete="off" />
            <label class="check"><input v-model="form(i).requeue" type="checkbox" /> Requeue the failed jobs</label>
            <span>
              <button class="btn btn-primary" type="submit" :disabled="!form(i).note">Resolve</button>
              {{ ' ' }}
              <button v-if="i.status === 'Open'" class="btn" type="button" :disabled="!form(i).note" @click="act(i, 'escalate')">Escalate</button>
            </span>
          </form>
        </div>
      </li>
      <li v-if="!loading && !issues.length && !error" class="muted">No open build issues.</li>
    </ul>
  </HaikuWindow>
</template>
