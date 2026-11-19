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
// Codes the server's external-login callback redirects back with.
const EXTERNAL_ERRORS = {
  external: 'Sign-in with the provider failed. Please try again.',
  locked: 'This account is locked or disabled.',
  noemail: 'The provider did not share an email address with us.',
  exists: 'An account with that email already exists. Sign in with your password instead.',
  create: 'Could not create an account. Please try again.',
}
const error = ref(EXTERNAL_ERRORS[route.query.error] ?? '')
const providers = ref([])
const registration = ref(false)
const needsCode = ref(false) // password accepted, second factor still due
const useRecovery = ref(false)
const code = ref('')
const unconfirmed = ref(false)
const notice = ref('')

onMounted(async () => {
  try { providers.value = await api('/auth/providers') } catch { providers.value = [] }
  try { registration.value = (await api('/auth/registration')).enabled } catch { registration.value = false }
})

/** Runs a sign-in request, mapping its errors to the message shown under the form. */
async function attempt(fn, unauthorized) {
  busy.value = true
  error.value = ''
  notice.value = ''
  unconfirmed.value = false
  try {
    await fn()
  } catch (e) {
    error.value = e.status === 401 ? unauthorized : e.message
    unconfirmed.value = e.status === 403 && /confirm/i.test(e.message)
  } finally {
    busy.value = false
  }
}

const submit = () => attempt(async () => {
  const res = await api('/auth/login', { method: 'POST', body: { email: email.value, password: password.value } })
  if (res.twoFactorRequired) { needsCode.value = true; return }
  store.user = res
  router.replace(returnUrl.value)
}, 'Invalid email or password.')

const submitCode = () => attempt(async () => {
  const c = code.value.replace(/\s/g, '')
  store.user = await api('/auth/login/2fa', { method: 'POST', body: useRecovery.value ? { recoveryCode: c } : { code: c } })
  router.replace(returnUrl.value)
}, 'Invalid code.')

function toggleRecovery() {
  useRecovery.value = !useRecovery.value
  code.value = ''
  error.value = ''
}

const resend = () => attempt(async () => {
  await api('/auth/resend-confirmation', { method: 'POST', body: { email: email.value } })
  notice.value = 'If that address needs confirming, we have emailed a new link to it.'
}, '')

const externalHref = (p) => `/api/auth/external/${encodeURIComponent(p)}?returnUrl=${encodeURIComponent(returnUrl.value)}`
</script>

<template>
  <div class="narrow">
    <HaikuWindow title="Sign in">
      <p v-if="store.user" class="muted">You are signed in as {{ store.user.email }}.</p>
      <form v-if="needsCode" class="form" @submit.prevent="submitCode">
        <template v-if="useRecovery">
          <label for="si-recovery">Recovery code</label>
          <input id="si-recovery" v-model="code" autocomplete="off" autocapitalize="off" spellcheck="false" required />
        </template>
        <template v-else>
          <label for="si-code">Authenticator code</label>
          <input id="si-code" v-model="code" inputmode="numeric" pattern="\s*\d{3}\s*\d{3}\s*" maxlength="7" autocomplete="one-time-code" required />
        </template>
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy">{{ busy ? 'Checking…' : 'Verify' }}</button>
        <p><button type="button" class="btn btn-small" @click="toggleRecovery">{{ useRecovery ? 'Use an authenticator code instead' : 'Use a recovery code instead' }}</button></p>
      </form>
      <form v-else class="form" @submit.prevent="submit">
        <label for="si-email">Email</label>
        <input id="si-email" v-model="email" type="email" autocomplete="username" required />
        <label for="si-password">Password</label>
        <input id="si-password" v-model="password" type="password" autocomplete="current-password" required />
        <p v-if="error" class="error" role="alert">{{ error }}</p>
        <p v-if="unconfirmed"><button type="button" class="btn btn-small" :disabled="busy" @click="resend">Resend confirmation email</button></p>
        <p v-if="notice" class="notice" role="status">{{ notice }}</p>
        <button class="btn btn-primary" type="submit" :disabled="busy">{{ busy ? 'Signing in…' : 'Sign in' }}</button>
        <p class="hint small"><RouterLink to="/forgot-password">Forgot password?</RouterLink><RouterLink v-if="registration" to="/register">Register</RouterLink></p>
      </form>
      <template v-if="providers.length && !needsCode">
        <p class="divider"><span>or</span></p>
        <div class="providers">
          <a v-for="p in providers" :key="p" class="btn" :href="externalHref(p)">Continue with {{ p }}</a>
        </div>
      </template>
    </HaikuWindow>
  </div>
</template>
