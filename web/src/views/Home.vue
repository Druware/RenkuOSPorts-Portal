<script setup>
import { ref, onMounted } from 'vue'
import { api, formatBytes, formatDate, PLATFORMS } from '../api.js'

const LABELS = { x86: '32-bit x86', x86_64: '64-bit x86', arm64: 'ARM 64-bit (AArch64)', riscv64: 'RISC-V 64-bit' }
const platforms = ref(PLATFORMS.map((platform) => ({ platform })))
const error = ref('')

onMounted(async () => {
  try {
    platforms.value = await api('/platforms')
  } catch (e) {
    error.value = `Could not load repository statistics: ${e.message}`
  }
})
</script>

<template>
  <section class="hero">
    <h1><img src="/hero.png" alt="RenkuOS — connect, create, continue" width="2135" height="736" /></h1>
    <p class="lead">
      <strong>RenkuOSPorts</strong> is the RenkuOS fork of HaikuPorts. Packages are built from the ports recipes by a fleet of
      community build clients and published here for every supported platform.
    </p>
  </section>

  <p v-if="error" class="error" role="alert">{{ error }}</p>

  <div class="tiles">
    <RouterLink v-for="p in platforms" :key="p.platform" :to="`/repo/${p.platform}`" class="tile">
      <span class="win-tab tile-tab"><span class="win-close" aria-hidden="true"></span><span class="tile-name">{{ p.platform }}</span></span>
      <span class="win-body tile-body">
        <span class="muted">{{ LABELS[p.platform] || p.platform }}</span>
        <span class="tile-stat"><strong>{{ p.packageCount ?? '—' }}</strong> packages</span>
        <span>{{ formatBytes(p.totalBytes) }}</span>
        <span class="muted small">Last upload: {{ formatDate(p.lastUploadAt) }}</span>
      </span>
    </RouterLink>
  </div>
</template>
