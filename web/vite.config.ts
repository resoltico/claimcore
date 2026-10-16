import { fileURLToPath } from "node:url";
import { availableParallelism } from "node:os";
import react from "@vitejs/plugin-react";
import type { UserConfig } from "vite";
import ContractFirstSequencer from "./scripts/contract-first-sequencer.mjs";
import RequiredTestReporter from "./scripts/required-test-reporter.mjs";

const testWorkers = Math.max(1, Math.min(6, Math.ceil(availableParallelism() / 3)));

// Vitest 5.0.0's config declarations do not pass strict library checking. Vite-owned keys remain
// context-checked here, and required zero-warning Vitest runs exercise the test block.
export default {
  plugins: [react()],
  resolve: {
    alias: [
      { find: "@contracts", replacement: fileURLToPath(new URL("../contracts", import.meta.url)) },
      {
        find: /^@formatjs\/icu-messageformat-parser$/u,
        replacement: "@formatjs/icu-messageformat-parser/no-parser.js",
      },
    ],
  },
  build: {
    chunkSizeWarningLimit: 600,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [{ name: "presentation-catalogs", test: /\/src\/presentation\/generated\//u }],
        },
      },
    },
    target: "es2022",
    sourcemap: false,
  },
  server: {
    host: "127.0.0.1",
  },
  test: {
    sequence: { sequencer: ContractFirstSequencer },
    environment: "jsdom",
    maxWorkers: testWorkers,
    pool: "vmThreads",
    include: ["tests/**/*.test.{ts,tsx}"],
    setupFiles: "./tests/setup.ts",
    allowOnly: false,
    retry: 0,
    reporters: ["default", new RequiredTestReporter()],
    coverage: {
      provider: "v8",
      reporter: ["text", "json-summary"],
      reportsDirectory: "../artifacts/frontend/coverage",
      include: ["src/**/*.{ts,tsx}", "src/presentation/pseudolocalization.mjs"],
    },
  },
} satisfies UserConfig & { test: unknown };
