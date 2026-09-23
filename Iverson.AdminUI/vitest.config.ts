import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/vitest.setup.ts"],
    // Run test FILES serially. Not a workaround for a busy machine — the parallel
    // run is itself the load: measured on a 4-core box, load average sat at 2.6
    // after a serial run and climbed to 22 once default-parallelism vitest started.
    // Under that contention this suite's waitFor calls exceed the 5s default and
    // fail as timeouts in random files, which reads as flake and wastes review
    // rounds chasing it. --maxWorkers=2 was measured insufficient (still failed);
    // file-level serialisation was green across every run. Costs ~20s (70s -> 90s).
    // Tests within a file still run concurrently.
    fileParallelism: false,
    // Vitest's default mode ("test") doesn't load .env.development, so src/config.ts's
    // readConfig() has nothing to read unless these are supplied here.
    //
    // These are INDEPENDENT TEST FIXTURES, not a mirror of .env.development, and they
    // are not required to track it. They only have to be values the tests assert on:
    // client.test.ts and telemetry.test.ts both expect http://localhost:8080/... , so
    // VITE_API_BASE_URL below is 8080 while .env.development is deliberately 8081.
    //
    // DO NOT "resync" this the other way — do not copy 8080 onto .env.development. 8080
    // is the API's gRPC listener, configured Protocols: Http2, which a browser cannot
    // speak in cleartext; 8081 is the HTTP/1.1 listener the console's fetch calls need.
    // Pointing .env.development back at 8080 is exactly the breakage the 8081 value fixed.
    env: {
      VITE_OIDC_CLIENT_ID: "test-oidc-client-id",
      VITE_OIDC_AUTHORITY: "http://localhost:9000/application/o/test/",
      VITE_API_BASE_URL: "http://localhost:8080",
    },
  },
});
