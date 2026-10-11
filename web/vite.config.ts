import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

// The .NET API dev URL.
const apiProxy = 'http://localhost:5050';

export default defineConfig({
  plugins: [react()],
  build: {
    // The .NET service serves the built app from wwwroot at the site root; the API lives at /api.
    outDir: path.resolve(here, '../src/CobaltMrp.Api/wwwroot'),
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: apiProxy,
        changeOrigin: true,
      },
    },
  },
});
