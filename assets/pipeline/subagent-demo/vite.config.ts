import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import { viteSingleFile } from 'vite-plugin-singlefile';

const root = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  root,
  base: './',
  plugins: [viteSingleFile()],
  build: {
    // Emits assets/preview/subagent-demo.html next to state-demo.html.
    outDir: fileURLToPath(new URL('../../preview', import.meta.url)),
    // Never wipe assets/preview: it holds other hand-maintained demo pages.
    emptyOutDir: false,
    copyPublicDir: false,
    rollupOptions: {
      input: fileURLToPath(new URL('subagent-demo.html', import.meta.url)),
    },
  },
});
