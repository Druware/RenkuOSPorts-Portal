<script setup>
defineProps({ status: { type: String, required: true } })
</script>

<template>
  <span class="badge" :class="`badge-${status}`">{{ { InProgress: 'In progress', AwaitingOverride: 'Awaiting override' }[status] ?? status }}</span>
</template>
