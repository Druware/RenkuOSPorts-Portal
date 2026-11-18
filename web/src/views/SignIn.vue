<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, store, safeReturnUrl } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'

const route = useRoute()
const router = useRouter()
const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))

const email = ref('')
const password = ref('')
const busy = ref(false)
const error = ref('')
const providers = ref([])

onMounted(async () => {
  try { providers.value = await api('/auth/providers') } catch { providers.value = [] }
})

async function submit() {
  busy.value = true
  error.value = ''
  try {
    store.user = await api('/auth/login', { method: 'POST', body: { email: email.value, password: password.value } })
    router.replace(returnUrl.value)
  } catch (e) {
    error.value = e.status === 401 ? 'Invalid email or password.' : e.message
  } finally {
    busy.value = false
  }
}

const externalHref = (p) => `/api/auth/external/${encodeURIComponent(p)}?returnUrl=${encodeURIComponent(returnUrl.value)}`
</script>

<template>
  <div class="narrow">
    <HaikuWindow title="Sign in">
      <p v-if="store.user" class="muted">You are signed in as {{ store.user.email }}.</p>
      <form class="form" @submit.prevent="submit">
        <label for="si-email">Email</label>
        <input id="si-email" v-model="email" type="email" autocomplete="username" required />
        <label for="si-password">Password</label>
        <input id="si-password" v-model="password" type="password" autocomplete="current-password" required />
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy">{{ busy ? 'Signing in…' : 'Sign in' }}</button>
      </form>
      <template v-if="providers.length">
        <p class="divider"><span>or</span></p>
        <div class="providers">
          <a v-for="p in providers" :key="p" class="btn" :href="externalHref(p)">Continue with {{ p }}</a>
        </div>
      </template>
    </HaikuWindow>
  </div>
</template>
