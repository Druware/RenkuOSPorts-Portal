<script setup>
import { ref, reactive, onMounted } from 'vue'
import QRCode from 'qrcode'
import { api, store } from '../api.js'
import HaikuWindow from '../components/HaikuWindow.vue'
import CopyButton from '../components/CopyButton.vue'

// ---- Password ----
const pw = reactive({ currentPassword: '', newPassword: '', confirm: '' })
const pwBusy = ref(false)
const pwError = ref('')
const pwDone = ref(false)
async function changePassword() {
  pwDone.value = false
  if (pw.newPassword !== pw.confirm) { pwError.value = 'The new passwords do not match.'; return }
  pwBusy.value = true
  pwError.value = ''
  try {
    await api('/auth/password', { method: 'POST', body: { currentPassword: pw.currentPassword, newPassword: pw.newPassword } })
    Object.assign(pw, { currentPassword: '', newPassword: '', confirm: '' })
    pwDone.value = true
  } catch (e) {
    pwError.value = e.message
  } finally {
    pwBusy.value = false
  }
}

// ---- Two-factor authentication ----
const mfa = ref(null) // { enabled, recoveryCodesLeft }
const setup = ref(null) // { sharedKey, otpauthUri, qr } while setting up
const recoveryCodes = ref(null) // shown once, right after enable or regenerate
const code = ref('')
const mfaBusy = ref(false)
const mfaError = ref('')
const mfaNotice = ref('')

async function loadMfa() {
  try { mfa.value = await api('/auth/mfa') } catch (e) { mfaError.value = e.message }
}

/** Runs one MFA request with the shared busy/error state. */
async function mfaCall(fn) {
  mfaBusy.value = true
  mfaError.value = ''
  mfaNotice.value = ''
  try { await fn() } catch (e) { mfaError.value = e.message } finally { mfaBusy.value = false }
}

const startSetup = () => mfaCall(async () => {
  const s = await api('/auth/mfa/setup', { method: 'POST' })
  setup.value = { ...s, qr: await QRCode.toDataURL(s.otpauthUri, { margin: 2, width: 200 }) }
  recoveryCodes.value = null
  code.value = ''
})

const totp = () => code.value.replace(/\s/g, '')

const enable = () => mfaCall(async () => {
  recoveryCodes.value = (await api('/auth/mfa/enable', { method: 'POST', body: { code: totp() } })).recoveryCodes
  setup.value = null
  code.value = ''
  if (store.user) store.user.twoFactorEnabled = true
  await loadMfa()
})

const regenerate = () => mfaCall(async () => {
  recoveryCodes.value = (await api('/auth/mfa/recovery-codes', { method: 'POST', body: { code: totp() } })).recoveryCodes
  code.value = ''
  await loadMfa()
})

const disable = () => {
  if (!confirm('Turn off two-factor authentication? Your recovery codes stop working.')) return
  return mfaCall(async () => {
    await api('/auth/mfa/disable', { method: 'POST', body: { code: totp() } })
    code.value = ''
    recoveryCodes.value = null
    if (store.user) store.user.twoFactorEnabled = false
    mfaNotice.value = 'Two-factor authentication is off.'
    await loadMfa()
  })
}

onMounted(loadMfa)
</script>

<template>
  <h1 class="page-title">Account</h1>
  <p class="muted">Signed in as {{ store.user?.email }}.</p>

  <div class="narrow">
    <HaikuWindow title="Change password">
      <form class="form" @submit.prevent="changePassword">
        <input type="email" :value="store.user?.email" autocomplete="username" hidden />
        <label for="ac-current">Current password</label>
        <input id="ac-current" v-model="pw.currentPassword" type="password" autocomplete="current-password" required />
        <label for="ac-new">New password</label>
        <input id="ac-new" v-model="pw.newPassword" type="password" autocomplete="new-password" required />
        <label for="ac-confirm">Confirm new password</label>
        <input id="ac-confirm" v-model="pw.confirm" type="password" autocomplete="new-password" required />
        <p v-if="pwError" class="error" role="alert">{{ pwError }}</p>
        <p v-if="pwDone" class="notice" role="status">Your password has been changed.</p>
        <button class="btn btn-primary" type="submit" :disabled="pwBusy">{{ pwBusy ? 'Saving…' : 'Change password' }}</button>
        <p class="small muted">Signed up with GitHub and have no password? <RouterLink to="/forgot-password">Set one with Forgot password</RouterLink>.</p>
      </form>
    </HaikuWindow>

    <HaikuWindow title="Two-factor authentication">
      <p v-if="!mfa && !mfaError" class="muted">Loading…</p>

      <div v-if="recoveryCodes" class="secret" role="status">
        <p><strong>Your recovery codes.</strong> Save them somewhere safe now — they will not be shown again. Each works once if you lose your authenticator.</p>
        <pre class="cmd">{{ recoveryCodes.join('\n') }}</pre>
        <p><CopyButton :text="recoveryCodes.join('\n')" label="Copy codes" /> <button type="button" class="btn btn-small" @click="recoveryCodes = null">Dismiss</button></p>
      </div>

      <template v-if="mfa && !mfa.enabled">
        <form v-if="setup" class="form" @submit.prevent="enable">
          <p>Scan this code with your authenticator app, or enter the key by hand.</p>
          <img :src="setup.qr" width="200" height="200" alt="QR code for your authenticator app" />
          <p class="secret-row"><span>Key</span> <code>{{ setup.sharedKey }}</code> <CopyButton :text="setup.sharedKey" label="Copy key" /></p>
          <label for="ac-code">Code from the app</label>
          <input id="ac-code" v-model="code" inputmode="numeric" pattern="\s*\d{3}\s*\d{3}\s*" maxlength="7" autocomplete="one-time-code" required />
          <p v-if="mfaError" class="error" role="alert">{{ mfaError }}</p>
          <p class="hint">
            <button class="btn btn-primary" type="submit" :disabled="mfaBusy">Turn on</button>
            <button type="button" class="btn" @click="setup = null">Cancel</button>
          </p>
        </form>
        <template v-else>
          <p>Two-factor authentication is <strong>off</strong>. Turn it on to ask for a code from an authenticator app when you sign in with your password.</p>
          <p v-if="mfaNotice" class="notice" role="status">{{ mfaNotice }}</p>
          <p v-if="mfaError" class="error" role="alert">{{ mfaError }}</p>
          <button type="button" class="btn btn-primary" :disabled="mfaBusy" @click="startSetup">Set up authenticator</button>
        </template>
      </template>

      <form v-else-if="mfa" class="form" @submit.prevent>
        <p>Two-factor authentication is <strong>on</strong>. {{ mfa.recoveryCodesLeft }} recovery code{{ mfa.recoveryCodesLeft === 1 ? '' : 's' }} left.</p>
        <label for="ac-code2">Authenticator or recovery code</label>
        <input id="ac-code2" v-model="code" autocomplete="one-time-code" autocapitalize="off" spellcheck="false" required />
        <p v-if="mfaError" class="error" role="alert">{{ mfaError }}</p>
        <p class="hint">
          <button type="button" class="btn" :disabled="mfaBusy || !code.trim()" @click="regenerate">New recovery codes</button>
          <button type="button" class="btn btn-danger" :disabled="mfaBusy || !code.trim()" @click="disable">Turn off</button>
        </p>
      </form>
      <p v-else-if="mfaError" class="error" role="alert">{{ mfaError }}</p>
    </HaikuWindow>
  </div>
</template>
