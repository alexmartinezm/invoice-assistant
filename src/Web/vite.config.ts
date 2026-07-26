import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // The API serves the SPA from wwwroot in production, so there is a single container to deploy.
    outDir: '../Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      // Same-origin in dev as in production: no CORS, and the SSE stream is proxied untouched.
      '/api': {
        target: process.env.API_URL ?? 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
});
