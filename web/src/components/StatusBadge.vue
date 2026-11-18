<script setup>
defineProps({ status: { type: String, required: true } })
</script>

<template>
  <span class="badge" :class="`badge-${status}`">{{ status === 'InProgress' ? 'In progress' : status }}</span>
</template>
