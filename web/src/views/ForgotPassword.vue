<script setup>
import { ref } from 'vue'
import { api } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'

const email = ref('')
const busy = ref(false)
const error = ref('')
const done = ref(false)

async function submit() {
  busy.value = true
  error.value = ''
  try {
    await api('/auth/forgot-password', { method: 'POST', body: { email: email.value } })
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
    <HaikuWindow title="Forgot password">
      <!-- The same reply whether or not the account exists, so it cannot be probed. -->
      <p v-if="done" class="notice" role="status">If that address can be used, we've emailed it a link to reset your password. <RouterLink to="/signin">Sign in</RouterLink></p>
      <form v-else class="form" @submit.prevent="submit">
        <label for="fp-email">Email</label>
        <input id="fp-email" v-model="email" type="email" autocomplete="email" required />
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy">{{ busy ? 'Sending…' : 'Email me a reset link' }}</button>
      </form>
    </HaikuWindow>
  </div>
</template>
