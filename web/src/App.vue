<script setup>
import { watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { store, api, isAdmin } from './api.js'

const route = useRoute()
const router = useRouter()

// A 401 anywhere clears the user; leave protected pages when that happens.
watch(() => store.user, (user) => {
  if (!user && store.loaded && route.meta.auth) router.replace({ path: '/signin', query: { returnUrl: route.fullPath } })
})

async function signOut() {
  try { await api('/auth/logout', { method: 'POST' }) } finally {
    store.user = null
    router.push('/')
  }
}
</script>

<template>
  <a class="skip" href="#main">Skip to content</a>
  <header class="site-header">
    <RouterLink to="/" class="brand" aria-label="RenkuOS Ports home">
      <img src="/logo.png" alt="" width="93" height="32" />
      <span>Ports</span>
    </RouterLink>
    <nav aria-label="Main">
      <RouterLink to="/repo/x86_64" :class="{ 'router-link-active': route.path.startsWith('/repo') }">Packages</RouterLink>
      <RouterLink v-if="store.user" to="/clients">Build clients</RouterLink>
      <RouterLink v-if="isAdmin()" to="/admin">Admin</RouterLink>
      <template v-if="store.user">
        <span class="who" :title="store.user.email">{{ store.user.displayName || store.user.email }}</span>
        <button type="button" class="btn btn-small" @click="signOut">Sign out</button>
      </template>
      <RouterLink v-else :to="{ path: '/signin', query: { returnUrl: route.fullPath } }">Sign in</RouterLink>
    </nav>
  </header>
  <main id="main" tabindex="-1">
    <RouterView />
  </main>
  <footer class="site-footer">
    <span>RenkuOSPorts — the RenkuOS fork of HaikuPorts.</span>
    <a href="https://renkuos.org">renkuos.org</a>
  </footer>
</template>
