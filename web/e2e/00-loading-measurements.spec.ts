import { test, expect, type Page, type Browser } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { preferenceKey } from "../src/presentation/preferences";
import { openCase } from "./case-workflow";
import { selectLanguage } from "./localization-support";

type Resource = {
  path: string;
  kind: string;
  transfer: number;
  encoded: number;
  decoded: number;
  duration: number;
};
const resources = (page: Page): Promise<Resource[]> =>
  page.evaluate(() =>
    performance
      .getEntriesByType("resource")
      .filter((entry) => entry instanceof PerformanceResourceTiming)
      .filter(
        (entry) =>
          entry.initiatorType === "script" ||
          entry.initiatorType === "link" ||
          new URL(entry.name).pathname.startsWith("/assets/"),
      )
      .map((entry) => ({
        path: new URL(entry.name).pathname,
        kind: entry.initiatorType,
        transfer: entry.transferSize,
        encoded: entry.encodedBodySize,
        decoded: entry.decodedBodySize,
        duration: entry.duration,
      })),
  );
const authenticated = async (browser: Browser, language: string, state: unknown) => {
  if (
    typeof state !== "object" ||
    state === null ||
    !("cookies" in state) ||
    !Array.isArray(state.cookies)
  ) {
    throw new Error("Measurement requires the owned synthetic cookie state");
  }
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  await context.addCookies(state.cookies);
  await context.addInitScript(
    ({ key, language: selected }) => {
      localStorage.setItem(
        key,
        JSON.stringify({ version: 2, language: selected, displayLocale: "en-GB", theme: "SYSTEM" }),
      );
    },
    { key: preferenceKey, language },
  );
  return context;
};

const heading = (page: Page) =>
  page
    .locator("main h1:visible")
    .filter({ hasNotText: /^ClaimCore$/u })
    .first();
const reference = "LOADING-COMMON-CASE";
const ready = async (page: Page, route: string) => {
  await expect(heading(page)).toBeVisible();
  if (route === "/cases") {
    await expect(
      page.locator(".worklist-rows").getByRole("link", { name: reference, exact: true }),
    ).toBeVisible();
  }
};
type Measurements = unknown[];
const measureLanguages = async (page: Page, observations: Measurements) => {
  for (const language of ["lv", "ar", "en"] as const) {
    const count = (await resources(page)).length;
    const started = performance.now();
    await selectLanguage(page, language);
    const rendered = performance.now() - started;
    await page.waitForLoadState("networkidle");
    observations.push({
      task: "language-activation",
      language,
      renderMilliseconds: rendered,
      resources: (await resources(page)).slice(count),
    });
  }
};
const measureNavigation = async (
  page: Page,
  baseURL: string,
  language: string,
  observations: Measurements,
) => {
  for (const route of ["/operations", "/authority", "/cases"]) {
    const before = await heading(page).textContent();
    const count = (await resources(page)).length;
    const started = performance.now();
    await page.locator(`.workspace-header nav a[href="${route}"]`).click();
    await expect(page).toHaveURL(new URL(route, baseURL).href);
    await expect.poll(() => heading(page).textContent()).not.toBe(before);
    await ready(page, route);
    const rendered = performance.now() - started;
    await page.waitForLoadState("networkidle");
    observations.push({
      task: "warm-navigation",
      language,
      route,
      renderMilliseconds: rendered,
      resources: (await resources(page)).slice(count),
    });
  }
  const count = (await resources(page)).length;
  const started = performance.now();
  await page.locator(".worklist-rows").getByRole("link", { name: reference, exact: true }).click();
  await expect(page.locator("section.case-fields")).toContainText(reference);
  const rendered = performance.now() - started;
  await page.waitForLoadState("networkidle");
  observations.push({
    task: "warm-case-inspection",
    language,
    renderMilliseconds: rendered,
    resources: (await resources(page)).slice(count),
  });
  if (language === "en") {
    await measureLanguages(page, observations);
  }
};
const measureRoute = async (
  browser: Browser,
  baseURL: string,
  state: unknown,
  language: string,
  route: string,
  observations: Measurements,
) => {
  const context = await authenticated(browser, language, state);
  const errors: string[] = [];
  const page = await context.newPage();
  try {
    page.on("response", (response) => {
      if (new URL(response.url()).pathname.startsWith("/assets/") && response.status() >= 400) {
        errors.push(
          `${new URL(response.url()).pathname}: ${response.status()}, ${response.headers()["content-type"]}, ${response.headers()["cache-control"]}`,
        );
        void response
          .text()
          .then((body) => {
            errors.push(`Asset error body: ${body.slice(0, 512)}`);
          })
          .catch(() => {
            errors.push("Asset error body unavailable");
          });
      }
    });
    page.on("requestfailed", (request) => {
      if (new URL(request.url()).pathname.startsWith("/assets/")) {
        errors.push(`${new URL(request.url()).pathname}: ${request.failure()?.errorText}`);
      }
    });
    page.on("pageerror", (error) => {
      errors.push(error.message);
    });
    const started = performance.now();
    await page.goto(new URL(route, baseURL).href, { waitUntil: "commit" });
    await ready(page, route);
    await expect(page.locator("html")).toHaveAttribute("lang", language);
    const rendered = performance.now() - started;
    await page.waitForLoadState("networkidle");
    observations.push({
      task: "cold-route",
      language,
      route,
      renderMilliseconds: rendered,
      resources: await resources(page),
      visibleCaseRows: await page.locator(".worklist-rows > tr").count(),
    });
    if (route === "/cases") {
      await measureNavigation(page, baseURL, language, observations);
    }
  } catch (error) {
    errors.push(`Visible alerts: ${(await page.getByRole("alert").allTextContents()).join("; ")}`);
    throw new Error(
      `Loading measurement ${language} ${route}; script failures: ${errors.join("; ")}`,
      { cause: error },
    );
  } finally {
    await context.close();
  }
};
test("records comparable cold routes warm navigation and language activation", async ({
  browser,
  baseURL,
}) => {
  test.setTimeout(120_000);
  const directory = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (directory === undefined || baseURL === undefined) {
    throw new Error("Owned measurement output and service origin are required");
  }
  const state: unknown = JSON.parse(
    await readFile(resolve(directory, "authenticated-state.json"), "utf8"),
  );
  // Setup is outside every measured browser context. Both revisions use their maintained
  // OPEN UI driver with the same eight independently specified business values.
  const seed = await authenticated(browser, "en", state);
  try {
    const page = await seed.newPage();
    await page.goto(new URL("/cases", baseURL).href);
    await expect(heading(page)).toBeVisible();
    await openCase(page, reference);
  } finally {
    await seed.close();
  }
  const observations: Measurements = [];
  for (const language of ["en", "lv", "ar"]) {
    for (const route of ["/cases", "/operations", "/authority"]) {
      await measureRoute(browser, baseURL, state, language, route, observations);
    }
  }
  await writeFile(
    resolve(directory, "loading-measurements.json"),
    JSON.stringify(
      {
        schema: 3,
        engine: test.info().project.name,
        timing:
          "Usable area heading plus common case row for Cases; case inspection waits for complete case fields; all render timings exclude resource-idle wait; all requested asset resources collected after idle",
        workload: {
          reference,
          cases: 1,
          registration: {
            incidentDate: "2026-09-01",
            notificationDate: "2026-09-02",
            country: "LV",
            claimant: "Synthetic claimant",
            insurer: "Synthetic insurer",
            amount: "1200.50",
            currency: "EUR",
          },
        },
        observations,
      },
      null,
      2,
    ),
  );
  expect(observations).toHaveLength(24);
});
