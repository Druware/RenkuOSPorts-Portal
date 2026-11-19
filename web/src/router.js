import { createRouter, createWebHistory } from 'vue-router'
import { store, loadMe, isAdmin } from './api.js'
import Home from './views/Home.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: Home, meta: { title: 'Home' } },
    { path: '/repo/:platform', component: () => import('./views/Repo.vue'), meta: { title: 'Packages' } },
    { path: '/signin', component: () => import('./views/SignIn.vue'), meta: { title: 'Sign in' } },
    { path: '/register', component: () => import('./views/Register.vue'), meta: { title: 'Register' } },
    { path: '/confirm-email', component: () => import('./views/ConfirmEmail.vue'), meta: { title: 'Confirm email' } },
    { path: '/forgot-password', component: () => import('./views/ForgotPassword.vue'), meta: { title: 'Forgot password' } },
    { path: '/reset-password', component: () => import('./views/ResetPassword.vue'), meta: { title: 'Reset password' } },
    { path: '/account', component: () => import('./views/Account.vue'), meta: { title: 'Account', auth: true } },
    { path: '/clients', component: () => import('./views/Clients.vue'), meta: { title: 'Build clients', auth: true } },
    { path: '/admin', component: () => import('./views/Admin.vue'), meta: { title: 'Ports dashboard', auth: true, admin: true } },
    { path: '/:rest(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async (to) => {
  if (!store.loaded) await loadMe()
  if (to.meta.auth && !store.user) return { path: '/signin', query: { returnUrl: to.fullPath } }
  if (to.meta.admin && !isAdmin()) return '/'
})

router.afterEach((to) => {
  document.title = `${to.meta.title} · RenkuOS Ports`
})

export default router
