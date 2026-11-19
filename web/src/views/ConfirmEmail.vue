<script setup>
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { api } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'

const route = useRoute()
const { userId, token } = route.query
// Wait for a click: mail scanners open links (some run JS), which must not confirm an address.
const state = ref(userId && token ? 'ready' : 'failed') // ready | busy | done | failed
const error = ref(state.value === 'failed' ? 'Invalid or expired link' : '')

async function confirmEmail() {
  state.value = 'busy'
  try {
    await api('/auth/confirm-email', { method: 'POST', body: { userId, token } })
    state.value = 'done'
  } catch (e) {
    state.value = 'failed'
    error.value = e.message
  }
}
</script>

<template>
  <div class="narrow">
    <HaikuWindow title="Confirm email">
      <button v-if="state === 'ready' || state === 'busy'" type="button" class="btn btn-primary" :disabled="state === 'busy'" @click="confirmEmail">{{ state === 'busy' ? 'Confirming…' : 'Confirm my email address' }}</button>
      <p v-else-if="state === 'done'" class="notice" role="status">Your email address is confirmed. <RouterLink to="/signin">Sign in</RouterLink></p>
      <template v-else>
        <p class="error" role="alert">{{ error }}</p>
        <p class="small">Sign in to request a new confirmation email. <RouterLink to="/signin">Sign in</RouterLink></p>
      </template>
    </HaikuWindow>
  </div>
</template>
