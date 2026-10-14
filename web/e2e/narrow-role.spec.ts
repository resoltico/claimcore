import { refusedRead } from "./narrow-role-oracles";
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
  await page.getByLabel("Handler's case reference (exact)").fill(reference);
  await page.getByRole("button", { name: "Find case", exact: true }).click();
};

const expectDefinitionAccess = async (page: Page, role: NarrowRole | null, scoped: boolean) => {
  const token = await sessionToken(page);
  const definition = await browserRequest(page, "/api/definition");
  expect(definition.status).toBe(200);
  expect(definition.payload).toMatchObject({
    outcome: { tag: role === "CASE_READER" && !scoped ? "DESCRIBED" : "REJECTED" },
  });
  expect(token.length).toBeGreaterThan(0);
};

const readCase = async (page: Page, fixture: RoleFixture, role: NarrowRole, scoped: boolean) => {
  await expect(page.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
  await findCase(page, fixture.reference);
  await expect(page.getByRole("region", { name: "current case", exact: true })).toBeVisible();
  await expect(page.locator(".history-list li")).toHaveCount(1);
  await expect(page.locator(".history-list details")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^Close the case:/u })).toHaveCount(
    role === "CASE_EDITOR" ? 1 : 0,
  );
  if (role === "CASE_READER") {
    await refusedRead(page, "case.history", () =>
      page
        .getByLabel("History detail", { exact: true })
        .selectOption("FULL")
        .then(() => undefined),
    );
    await expect(page.locator(".history-list li")).toHaveCount(0);
  }
  if (scoped) {
    await page.getByRole("button", { name: "Back to cases", exact: true }).click();
    await refusedRead(page, "case.get", () => findCase(page, fixture.unrelated));
    await expect(page.getByRole("region", { name: "current case", exact: true })).toHaveCount(0);
  }
};

const cases: ReadonlyArray<readonly [string, NarrowRole | null, boolean]> = [
  ["installation reader", "CASE_READER", false],
  ["case reader", "CASE_READER", true],
  ["case editor", "CASE_EDITOR", true],
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
  test(`admits the ${name} workflow without wider grants through published Web [CC-AUTH-001]`, async ({
    page,
    browser,
  }) => {
    const fixture = await prepareRoleFixture(page);
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
        await context.close();
      }
    };
    setup = configureRole(page, fixture, role, scoped, (cleanup) => {
      restore = cleanup;
    });
    await setup;
    const narrow = await context.newPage();
    await openAuthenticated(narrow, "Cases", "narrow-authenticated-state.json");
    await expectDefinitionAccess(narrow, role, scoped);
    if (role === "CASE_READER" || role === "CASE_EDITOR") {
      await readCase(narrow, fixture, role, scoped);
    } else {
      await expect(narrow.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
      await refusedRead(narrow, "case.get", () => findCase(narrow, fixture.reference));
      if (role === "DATA_STEWARD") {
        await narrow.getByText("Disposition and privacy status", { exact: true }).click();
        await expect(narrow.getByText("Disposition: Active", { exact: true })).toBeVisible();
      }
    }
    await expectAccessible(narrow);
  });
}
