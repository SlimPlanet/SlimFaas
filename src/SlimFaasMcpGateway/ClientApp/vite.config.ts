import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // Proxy API calls to the backend during development
      '/api': {
        target: 'http://localhost:5269',
        changeOrigin: true,
        secure: false,
      },
      '/gateway': {
        target: 'http://localhost:5269',
        changeOrigin: true,
        secure: false,
      },
      '/health': {
        target: 'http://localhost:5269',
        changeOrigin: true,
        secure: false,
      },
      '/metrics': {
        target: 'http://localhost:5269',
        changeOrigin: true,
        secure: false,
      },
    },
  },
  build: {
    outDir: "dist",
    sourcemap: true,
    emptyOutDir: true,
  },
  base: "/",
});
