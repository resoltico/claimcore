import { showCaseActionMenu, caseActionButton } from "./case-workflow";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { showExactLookup } from "./workspace-navigation";
import {
  refusedRead,
  metadataTimeline,
  noCaseWorkspace,
  opaqueCaseRefusal,
} from "./narrow-role-oracles";
import { expect, test, type Page } from "@playwright/test";
import {
  openAuthenticated,
  authenticateSteward,
  logout,
  browserRequest,
  sessionToken,
  expectAccessible,
} from "./session-helpers";
import {
  prepareRoleFixture,
  configureRole,
  type NarrowRole,
  type RoleFixture,
} from "./narrow-role-fixture";
import { isResponse } from "../src/protocol/client";

let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>> | undefined;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});

const openNarrowSession = (page: Page, heading: string): Promise<void> => {
  if (stewardCookies === undefined) {
    throw new Error("E2E_NARROW_SPEC_SESSION_NOT_INITIALIZED");
  }
  return openAuthenticated(page, heading, stewardCookies);
};

const findCase = async (page: Page, reference: string) => {
  await (await showExactLookup(page)).fill(reference);
  await page.getByRole("button", { name: "Open by exact reference", exact: true }).click();
};

const expectContextAccess = async (page: Page) => {
  const token = await sessionToken(page);
  const definition = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
    body: "{}",
  });
  expect(definition.status).toBe(200);
  expect(definition.payload).toMatchObject({
    outcome: { kind: "SUCCEEDED" },
  });
  expect(token.length).toBeGreaterThan(0);
};

const readCase = async (page: Page, fixture: RoleFixture, role: NarrowRole, scoped: boolean) => {
  await expect(page.getByRole("link", { name: "Open a case", exact: true })).toHaveCount(0);
  await findCase(page, fixture.reference);
  await expect(page.locator(".case-header-state")).toBeVisible();
  await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(1);
  const history = page.locator(".case-timeline-panel");
  await expect(history.locator(".region-timeline > li")).toHaveCount(1);
  if (role !== "CASE_HANDLER") {
    await expect(history).toContainText("You do not have access to these changes.");
    await expect(history).not.toContainText("Synthetic claimant");
    await expect(history).not.toContainText("Synthetic insurer");
  }
  await showCaseActionMenu(page);
  if (role === "CASE_HANDLER") {
    await expect(await caseActionButton(page, "CLOSE", "en")).toBeVisible();
  } else {
    await expect(
      page
        .getByRole("button")
        .and(page.locator('button[data-act="CLOSE"]'))
        .filter({ visible: true }),
    ).toHaveCount(0);
  }
  if (role === "CASE_READER" || role === "DATA_STEWARD") {
    await expect(history.locator('[data-entry$=".before"], [data-entry$=".after"]')).toHaveCount(0);
    await metadataTimeline(page, fixture.reference);
    await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(1);
  }
  if (scoped) {
    await page.getByRole("button", { name: "Back to cases", exact: true }).click();
    await refusedRead(page, "case.get", () => findCase(page, fixture.unrelated));
    await expect(
      page.locator(
        ".case-header-state, .entries-view [data-entry='claimantName'], .case-work, .case-timeline-panel",
      ),
    ).toHaveCount(0);
  }
};

const cases: ReadonlyArray<readonly [string, NarrowRole | null, boolean]> = [
  ["installation reader", "CASE_READER", false],
  ["case reader", "CASE_READER", true],
  ["case handler", "CASE_HANDLER", true],
  ["steward", "DATA_STEWARD", false],
  ["enabled actor without grants", null, false],
];
const teardown: { current: (() => Promise<void>) | null } = { current: null };
test.afterEach(async () => {
  const cleanup = teardown.current;
  teardown.current = null;
  if (cleanup !== null) {
    await cleanup();
  }
});
for (const [name, role, scoped] of cases) {
  test(`admits the ${name} workflow without wider grants through published Web`, async ({
    page,
    browser,
  }) => {
    const fixture = await test.step("Create owner cases", () => prepareRoleFixture(page));
    const context = await browser.newContext({
      baseURL: new URL(page.url()).origin,
      ignoreHTTPSErrors: true,
    });
    let restore: (() => Promise<void>) | null = null;
    let setup: Promise<void> | null = null;
    teardown.current = async () => {
      try {
        if (setup !== null) {
          await setup.catch(() => undefined);
        }
        if (restore !== null) {
          await restore();
        }
      } finally {
        await test.step("Close narrow browser context", () => context.close());
      }
    };
    setup = configureRole(page, fixture, role, scoped, (cleanup) => {
      restore = cleanup;
    });
    await test.step("Apply scoped role", () => setup ?? Promise.resolve());
    const narrow = await context.newPage();
    await test.step("Open narrow actor session", () =>
      openNarrowSession(narrow, role === null ? "You don’t have access yet" : "Cases"));
    await test.step("Check enabled actor context", () => expectContextAccess(narrow));
    if (role === "CASE_READER" || role === "CASE_HANDLER" || role === "DATA_STEWARD") {
      await test.step("Read current case and enforce history scope", () =>
        readCase(narrow, fixture, role, scoped));
    } else {
      await noCaseWorkspace(narrow);
      await expect(
        narrow.locator(
          ".entries-view [data-entry='claimantName'], .case-work, .case-timeline-panel",
        ),
      ).toHaveCount(0);
      await opaqueCaseRefusal(narrow, fixture.reference);
    }
    await test.step("Check narrow view accessibility", () => expectAccessible(narrow));
  });
}

test("a fresh steward session does not restore a revoked ticket", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  const revoked = await authenticateSteward(browser, baseURL);
  const context = page.context();
  await openAuthenticated(page, "Cases", revoked);
  await logout(page);
  const expectAuthenticated = async (expected: boolean) => {
    const reply = await browserRequest(page, "/api/session");
    expect(reply.status).toBe(200);
    if (!isResponse("session", reply.payload) || reply.payload.outcome.kind !== "SUCCEEDED") {
      throw new Error("E2E_STEWARD_SESSION_RESPONSE_INVALID");
    }
    expect(reply.payload.outcome.data.authenticated).toBe(expected);
  };
  await context.addCookies(revoked);
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.locator(".login-card h1")).toBeVisible();
  await expectAuthenticated(false);

  const fresh = await authenticateSteward(browser, baseURL);
  await openAuthenticated(page, "Cases", fresh);
  await expectAuthenticated(true);

  await context.addCookies(revoked);
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.locator(".login-card h1")).toBeVisible();
  await expectAuthenticated(false);
});
