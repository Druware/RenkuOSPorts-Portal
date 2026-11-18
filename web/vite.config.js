import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const backend = process.env.BACKEND || 'http://localhost:8080'

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/api': backend,
      // Only the download paths go to the backend; /repo/:platform itself is an SPA route.
      '^/repo/[^/]+/packages/': backend,
    },
  },
})
