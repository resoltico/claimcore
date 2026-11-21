import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test as base } from "@playwright/test";
import { observeTrustedTypes, type TrustedTypesViolation } from "./trusted-types-observer";

export { errors, expect } from "@playwright/test";
export type {
  BrowserContext,
  Frame,
  Locator,
  Page,
  Request,
  Response,
  TestInfo,
} from "@playwright/test";

export const test = base.extend<
  { _workspaceTrustedTypes: void },
  { trustedTypesViolations: TrustedTypesViolation[] | null }
>({
  trustedTypesViolations: [
    async ({ browserName }, provide) => {
      await provide(browserName === "chromium" ? [] : null);
    },
    { scope: "worker" },
  ],
  browser: [
    async ({ browser, trustedTypesViolations }, provide, info) => {
      if (trustedTypesViolations === null) {
        await provide(browser);
        return;
      }
      const { baseURL } = info.project.use;
      if (baseURL === undefined) {
        throw new Error("Expected the published workspace origin.");
      }

      const { origin } = new URL(baseURL);
      const newContext = browser.newContext.bind(browser);
      // The default Playwright context and explicit contexts, including those in hooks,
      // use this factory before any page can navigate.
      browser.newContext = async (options) => {
        const context = await newContext(options);
        await observeTrustedTypes(context, origin, trustedTypesViolations);
        return context;
      };

      try {
        await provide(browser);
      } finally {
        browser.newContext = newContext;
        if (trustedTypesViolations.length > 0) {
          const directory = join(info.project.outputDir, String(info.workerIndex));
          await mkdir(directory, { recursive: true, mode: 0o700 });
          await writeFile(
            join(directory, "trusted-types-worker.json"),
            JSON.stringify(trustedTypesViolations, null, 2),
            { mode: 0o600 },
          );
        }
      }
      if (trustedTypesViolations.length > 0) {
        throw new Error("Trusted Types violations in published workspace hooks.");
      }
    },
    { scope: "worker" },
  ],
  _workspaceTrustedTypes: [
    async ({ trustedTypesViolations }, provide, info) => {
      await provide();
      const observed = trustedTypesViolations?.splice(0) ?? [];
      if (observed.length > 0) {
        const path = info.outputPath("trusted-types-violations.json");
        await writeFile(path, JSON.stringify(observed, null, 2), { mode: 0o600 });
        await info.attach("trusted-types-violations", { path, contentType: "application/json" });
        expect(observed, "Trusted Types violations in the published workspace").toEqual([]);
      }
    },
    { auto: true },
  ],
});
