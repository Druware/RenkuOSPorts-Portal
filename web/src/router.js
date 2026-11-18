import { createRouter, createWebHistory } from 'vue-router'
import { store, loadMe, isAdmin } from './api.js'
import Home from './views/Home.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: Home, meta: { title: 'Home' } },
    { path: '/repo/:platform', component: () => import('./views/Repo.vue'), meta: { title: 'Packages' } },
    { path: '/signin', component: () => import('./views/SignIn.vue'), meta: { title: 'Sign in' } },
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
