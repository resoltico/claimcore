import { fileURLToPath } from "node:url";
import { availableParallelism } from "node:os";
import react from "@vitejs/plugin-react";
import type { UserConfig } from "vite";
import type { InlineConfig } from "vitest/node";
import RequiredTestReporter from "./scripts/required-test-reporter.mjs";

const testWorkers = Math.max(1, Math.min(6, Math.ceil(availableParallelism() / 3)));

export default {
  plugins: [react()],
  resolve: {
    alias: [
      { find: "@contracts", replacement: fileURLToPath(new URL("../contracts", import.meta.url)) },
      {
        find: "@locales",
        replacement: fileURLToPath(new URL("./locales", import.meta.url)),
      },
    ],
  },
  build: {
    chunkSizeWarningLimit: 600,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: "presentation-catalogs", test: /\/ClaimCore.Contract\/Localization\//u },
          ],
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
      include: ["src/**/*.{ts,tsx}"],
    },
  },
} satisfies UserConfig & { test: InlineConfig };
