import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/vitest.setup.ts"],
    // Vitest's default mode ("test") doesn't load .env.development, so tests
    // need these mirrored here to satisfy src/config.ts's readConfig().
    env: {
      VITE_OIDC_CLIENT_ID: "test-oidc-client-id",
      VITE_OIDC_AUTHORITY: "http://localhost:9000/application/o/test/",
      VITE_API_BASE_URL: "http://localhost:8080",
    },
  },
});
