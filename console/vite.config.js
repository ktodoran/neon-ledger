import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  // Relative, so the same build works at a domain root and under /neon-ledger/ on GitHub Pages.
  base: './',
  plugins: [react()],
  test: { include: ['test/**/*.test.{js,jsx}'] },
});
