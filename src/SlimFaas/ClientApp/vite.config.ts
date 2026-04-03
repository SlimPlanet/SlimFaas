import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: path.resolve(__dirname, '../wwwroot'),
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/status-functions': 'http://localhost:5000',
      '/wake-function': 'http://localhost:5000',
    },
  },
})

