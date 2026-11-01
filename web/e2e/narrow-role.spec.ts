import { showExactLookup } from "./workspace-navigation";
import { refusedRead, noCaseWorkspace, opaqueCaseRefusal } from "./narrow-role-oracles";
import { expect, test, type Page } from "@playwright/test";
import {
  openAuthenticated,
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
  await expect(page.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
  await findCase(page, fixture.reference);
  await expect(page.getByRole("region", { name: "Current case", exact: true })).toBeVisible();
  await expect(page.locator(".history-list li")).toHaveCount(1);
  const history = page.locator(".history-list");
  await expect(history.locator("details")).toHaveCount(1);
  await history.locator("details > summary").click();
  await expect(history.locator(".case-fields, .field-row")).toHaveCount(0);
  await expect(history).not.toContainText("Synthetic claimant");
  await expect(history).not.toContainText("Synthetic insurer");
  await expect(page.getByRole("button", { name: /^Close the case:/u })).toHaveCount(
    role === "CASE_HANDLER" ? 1 : 0,
  );
  if (role === "CASE_READER" || role === "DATA_STEWARD") {
    await expect(page.locator('select option[value="FULL"]')).toHaveCount(0);
    await refusedRead(page, "case.history", async () => {
      await browserRequest(page, "/api/case.history", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-ClaimCore-Antiforgery": await sessionToken(page),
        },
        body: JSON.stringify({
          target: { kind: "REFERENCE", caseReference: fixture.reference },
          detail: "FULL",
          limit: 50,
        }),
      });
    });
    await expect(page.locator(".history-list li")).toHaveCount(1);
  }
  if (scoped) {
    await page.getByRole("button", { name: "Back to cases", exact: true }).click();
    await refusedRead(page, "case.get", () => findCase(page, fixture.unrelated));
    await expect(page.getByRole("region", { name: "Current case", exact: true })).toHaveCount(0);
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
      openAuthenticated(
        narrow,
        role === null ? "Recovery" : "Cases",
        "narrow-authenticated-state.json",
      ));
    await test.step("Check enabled actor context", () => expectContextAccess(narrow));
    if (role === "CASE_READER" || role === "CASE_HANDLER" || role === "DATA_STEWARD") {
      await test.step("Read current case and enforce history scope", () =>
        readCase(narrow, fixture, role, scoped));
    } else {
      await noCaseWorkspace(narrow);
      await opaqueCaseRefusal(narrow, fixture.reference);
    }
    await test.step("Check narrow view accessibility", () => expectAccessible(narrow));
  });
}
