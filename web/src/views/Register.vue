<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'

const form = ref({ email: '', displayName: '', password: '', confirm: '' })
const enabled = ref(true)
const busy = ref(false)
const error = ref('')
const done = ref(false)

onMounted(async () => {
  try { enabled.value = (await api('/auth/registration')).enabled } catch { /* let the POST report it */ }
})

async function submit() {
  const f = form.value
  if (f.password !== f.confirm) { error.value = 'The passwords do not match.'; return }
  busy.value = true
  error.value = ''
  try {
    await api('/auth/register', { method: 'POST', body: { email: f.email, password: f.password, displayName: f.displayName.trim() } })
    done.value = true
  } catch (e) {
    if (e.status === 404) enabled.value = false
    else error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="narrow">
    <HaikuWindow title="Register">
      <p v-if="!enabled" class="muted">Registration is closed. <RouterLink to="/signin">Sign in</RouterLink></p>
      <!-- The same reply whether or not the address already has an account, so it cannot be probed. -->
      <p v-else-if="done" class="notice" role="status">If that address can be used, we've emailed it a link to confirm your account. <RouterLink to="/signin">Sign in</RouterLink></p>
      <form v-else class="form" @submit.prevent="submit">
        <label for="rg-email">Email</label>
        <input id="rg-email" v-model="form.email" type="email" autocomplete="email" required />
        <label for="rg-name">Display name</label>
        <input id="rg-name" v-model="form.displayName" autocomplete="nickname" required maxlength="100" />
        <label for="rg-password">Password</label>
        <input id="rg-password" v-model="form.password" type="password" autocomplete="new-password" required />
        <label for="rg-confirm">Confirm password</label>
        <input id="rg-confirm" v-model="form.confirm" type="password" autocomplete="new-password" required />
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy">{{ busy ? 'Registering…' : 'Register' }}</button>
        <p class="small">Already have an account? <RouterLink to="/signin">Sign in</RouterLink></p>
      </form>
    </HaikuWindow>
  </div>
</template>
