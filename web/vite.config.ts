import { fileURLToPath } from "node:url";
import { availableParallelism } from "node:os";
import publicAssets from "./scripts/public-assets.plugin.mjs";
import { attachmentPreviewResources } from "./scripts/attachment-viewer.mjs";
import react from "@vitejs/plugin-react";
import type { UserConfig } from "vite";
import type { InlineConfig } from "vitest/node";
import RequiredTestReporter from "./scripts/required-test-reporter.mjs";
import reactAriaLiveAnnouncer, {
  reactAriaLiveAnnouncerBanner,
} from "./scripts/react-aria-live-announcer.plugin.mjs";

const testWorkers = Math.max(1, Math.min(6, Math.ceil(availableParallelism() / 3)));

export default {
  cacheDir: "artifacts/vite",
  plugins: [reactAriaLiveAnnouncer(), react(), attachmentPreviewResources(), publicAssets()],
  optimizeDeps: { rolldownOptions: { plugins: [reactAriaLiveAnnouncer()] } },
  resolve: {
    alias: [
      { find: "@contracts", replacement: fileURLToPath(new URL("../contracts", import.meta.url)) },
    ],
  },
  build: {
    // Failed JS modulepreloads can poison WebKit's cache across reloads (https://bugs.webkit.org/show_bug.cgi?id=270357).
    // Native imports fetch dynamic JS; retain async CSS and initial HTML preloads.
    modulePreload: {
      resolveDependencies: (_filename, dependencies, { hostType }) =>
        hostType === "js" ? dependencies.filter((path) => path.endsWith(".css")) : dependencies,
    },
    chunkSizeWarningLimit: 600,
    rolldownOptions: {
      input: {
        workspace: fileURLToPath(new URL("./index.html", import.meta.url)),
        recovery: fileURLToPath(new URL("./auth-recovery.html", import.meta.url)),
      },
      output: {
        postBanner: reactAriaLiveAnnouncerBanner,
        codeSplitting: {
          groups: [{ name: "workspace", test: /\/web\/src\//u, entriesAware: true }],
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
    fsModuleCachePath: "artifacts/vitest",
    maxWorkers: testWorkers,
    pool: "vmThreads",
    include: ["tests/**/*.test.{ts,tsx}"],
    setupFiles: "./tests/support/setup.ts",
    server: { deps: { inline: ["react-aria-components", "react-aria"] } },
    allowOnly: false,
    retry: 0,
    reporters: ["default", new RequiredTestReporter()],
  },
} satisfies UserConfig & { test: InlineConfig };
