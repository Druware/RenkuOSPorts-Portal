<script setup>
import { computed } from 'vue'
const props = defineProps({
  page: { type: Number, required: true },
  pageSize: { type: Number, required: true },
  total: { type: Number, required: true },
  noun: { type: String, default: 'package' },
})
const emit = defineEmits(['update:page'])
const pages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
</script>

<!-- Previous / Next with "N packages · Page X of Y" under a paged list. -->
<template>
  <nav class="pager" aria-label="Pages">
    <button type="button" class="btn btn-small" :disabled="page <= 1" @click="emit('update:page', Math.min(page - 1, pages))">Previous</button>
    <span aria-live="polite">{{ total }} {{ noun }}{{ total === 1 ? '' : 's' }} · Page {{ page }} of {{ pages }}</span>
    <button type="button" class="btn btn-small" :disabled="page >= pages" @click="emit('update:page', page + 1)">Next</button>
  </nav>
</template>
