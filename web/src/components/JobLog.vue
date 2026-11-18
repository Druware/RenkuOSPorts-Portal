<script setup>
import { ref } from 'vue'
import { api } from '../api.js'
const props = defineProps({ jobId: { required: true } })
const log = ref(null)
const error = ref('')
async function onToggle(e) {
  if (!e.target.open || log.value !== null) return
  try {
    log.value = await api(`/jobs/${props.jobId}/log`, { text: true })
  } catch (err) {
    error.value = err.message
  }
}
</script>

<template>
  <details class="log" @toggle="onToggle">
    <summary>Build log</summary>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="log === null" class="muted">Loading…</p>
    <pre v-else>{{ log || '(empty log)' }}</pre>
  </details>
</template>
