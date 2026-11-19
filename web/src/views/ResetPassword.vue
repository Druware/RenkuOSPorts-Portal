<script setup>
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { api } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'

const route = useRoute()
const email = String(route.query.email || '')
const token = String(route.query.token || '')
const password = ref('')
const confirm = ref('')
const busy = ref(false)
const error = ref(email && token ? '' : 'Invalid or expired link')
const done = ref(false)

async function submit() {
  if (password.value !== confirm.value) { error.value = 'The passwords do not match.'; return }
  busy.value = true
  error.value = ''
  try {
    await api('/auth/reset-password', { method: 'POST', body: { email, token, password: password.value } })
    done.value = true
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="narrow">
    <HaikuWindow title="Reset password">
      <p v-if="done" class="notice" role="status">Your password has been reset. <RouterLink to="/signin">Sign in</RouterLink></p>
      <form v-else class="form" @submit.prevent="submit">
        <label for="rp-email">Email</label>
        <input id="rp-email" :value="email" type="email" autocomplete="username" readonly />
        <label for="rp-password">New password</label>
        <input id="rp-password" v-model="password" type="password" autocomplete="new-password" required />
        <label for="rp-confirm">Confirm new password</label>
        <input id="rp-confirm" v-model="confirm" type="password" autocomplete="new-password" required />
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy || !email || !token">{{ busy ? 'Saving…' : 'Set password' }}</button>
        <p class="small"><RouterLink to="/forgot-password">Request a new link</RouterLink></p>
      </form>
    </HaikuWindow>
  </div>
</template>
