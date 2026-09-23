import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
  },
  oxc: {
    target: 'es2022',
    decorator: { legacy: true, emitDecoratorMetadata: true },
  },
});
