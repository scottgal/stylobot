import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig(({ mode }) => ({
  root: process.cwd(),
  base: mode === 'development' ? '/' : '/dist/',
  build: {
    outDir: path.resolve(__dirname, 'wwwroot/dist'),
    emptyOutDir: false, // Don't empty - Tailwind CSS is already there!
    rollupOptions: {
      input: path.resolve(__dirname, 'wwwroot/src/main.ts'),
      output: {
        entryFileNames: 'assets/index.js',
        assetFileNames: 'assets/[name][extname]' // Don't output CSS - Tailwind handles it
      }
    }
  },
  server: {
    port: 5173,
    strictPort: true,
    // Proxy backend API to Kestrel (https) during development
    proxy: {
      '/': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
      }
    }
  }
}));

