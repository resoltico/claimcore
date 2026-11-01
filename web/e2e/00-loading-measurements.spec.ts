import { test, expect, type Page, type Browser } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { preferenceKey } from "../src/presentation/preferences";
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
    await expect(heading(page)).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", language);
    const rendered = performance.now() - started;
    await page.waitForLoadState("networkidle");
    observations.push({
      task: "cold-route",
      language,
      route,
      renderMilliseconds: rendered,
      resources: await resources(page),
      visibleCaseRows: await page.locator(".worklist-row").count(),
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
        schema: 2,
        engine: test.info().project.name,
        timing:
          "Area heading visible; warm navigation excludes resource-idle wait; resources collected after idle",
        observations,
      },
      null,
      2,
    ),
  );
  expect(observations).toHaveLength(21);
});
