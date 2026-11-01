import { fileURLToPath } from "node:url";
import { availableParallelism } from "node:os";
import publicAssets from "./scripts/public-assets.plugin.mjs";
import react from "@vitejs/plugin-react";
import type { UserConfig } from "vite";
import type { InlineConfig } from "vitest/node";
import RequiredTestReporter from "./scripts/required-test-reporter.mjs";

const testWorkers = Math.max(1, Math.min(6, Math.ceil(availableParallelism() / 3)));

export default {
  plugins: [react(), publicAssets()],
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
            // Keep modules with different entry owners out of each other's initial payload.
            { name: "workspace", test: /\/web\/src\//u, entriesAware: true },

            {
              name: "language-en",
              test: /\/(?:ClaimCore\.Protocol\/Messages\/Localization|web\/locales)\/en\.json/u,
            },
            {
              name: "language-lv",
              test: /\/(?:ClaimCore\.Protocol\/Messages\/Localization|web\/locales)\/lv\.json/u,
            },
            {
              name: "language-ar",
              test: /\/(?:ClaimCore\.Protocol\/Messages\/Localization|web\/locales)\/ar\.json/u,
            },
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
    setupFiles: "./tests/support/setup.ts",
    allowOnly: false,
    retry: 0,
    reporters: ["default", new RequiredTestReporter()],
  },
} satisfies UserConfig & { test: InlineConfig };
