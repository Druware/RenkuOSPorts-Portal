<script setup>
import { ref } from 'vue'
const props = defineProps({ text: { type: String, required: true }, label: { type: String, default: 'Copy' } })
const state = ref('')
async function copy() {
  try {
    await navigator.clipboard.writeText(props.text)
    state.value = 'Copied'
  } catch {
    state.value = 'Copy failed' // clipboard API needs a secure context (https or localhost)
  }
  setTimeout(() => (state.value = ''), 1500)
}
</script>

<template>
  <button type="button" class="btn btn-small" @click="copy" :aria-label="`${label}: ${text}`">
    <span aria-live="polite">{{ state || label }}</span>
  </button>
</template>
