import { availableParallelism } from "node:os";
import react from "@vitejs/plugin-react";
import type { UserConfig } from "vite";

const testWorkers = Math.max(1, Math.min(6, Math.ceil(availableParallelism() / 3)));

// Vitest 5.0.0's config declarations do not pass strict library checking. Vite-owned keys remain
// context-checked here, and required zero-warning Vitest runs exercise the test block.
export default {
  plugins: [react()],
  build: {
    chunkSizeWarningLimit: 600,
    target: "es2022",
    sourcemap: false,
  },
  server: {
    host: "127.0.0.1",
  },
  test: {
    environment: "jsdom",
    maxWorkers: testWorkers,
    pool: "vmThreads",
    include: ["tests/**/*.test.{ts,tsx}"],
    setupFiles: "./tests/setup.ts",
    reporters: ["./scripts/vitest-reporter.mjs"],
    coverage: {
      provider: "v8",
      reporter: ["text", "json-summary"],
      reportsDirectory: "../artifacts/frontend/coverage",
      include: ["src/**/*.{ts,tsx}"],
      thresholds: {
        statements: 90,
        branches: 85,
        functions: 90,
        lines: 90,
        perFile: true,
      },
    },
  },
} satisfies UserConfig & { test: unknown };
