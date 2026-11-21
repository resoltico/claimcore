import { type ResponseByEndpoint } from "@contracts/client";
import { showCaseActionMenu, caseActionButton } from "./case-workflow";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { showExactLookup } from "./workspace-navigation";
import {
  refusedRead,
  metadataTimeline,
  noCaseWorkspace,
  opaqueCaseRefusal,
} from "./narrow-role-oracles";
import { expect, test, type Page } from "./fixtures";
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
  await page
    .getByRole("button", {
      name: "Open by exact reference",
      exact: true,
    })
    .click();
};
const expectContextAccess = async (page: Page) => {
  const token = await sessionToken(page);
  const definition = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
    },
    body: "{}",
  });
  expect(definition.status).toBe(200);
  expect(definition.payload).toMatchObject({
    outcome: {
      kind: "SUCCEEDED",
    },
  });
  expect(token.length).toBeGreaterThan(0);
};
const readCase = async (page: Page, fixture: RoleFixture, role: NarrowRole, scoped: boolean) => {
  await expect(
    page.getByRole("link", {
      name: "Open a case",
      exact: true,
    }),
  ).toHaveCount(0);
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
    await expect(history.locator(".timeline-comparison [data-side=after]").first()).toBeAttached();
    await expect(await caseActionButton(page, "case.close", "en")).toBeVisible();
  } else {
    await expect(
      page.getByRole("button").and(page.locator('button[data-act="case.close"]')).filter({
        visible: true,
      }),
    ).toBeDisabled();
  }
  if (role === "CASE_READER" || role === "DATA_STEWARD") {
    await expect(history.locator(".timeline-comparison [data-side]")).toHaveCount(0);
    await metadataTimeline(page, fixture.reference);
    await expect(page.locator(".case-timeline-panel .region-timeline > li")).toHaveCount(1);
  }
  if (scoped) {
    await page
      .getByRole("button", {
        name: "Back to cases",
        exact: true,
      })
      .click();
    await refusedRead(page, "case.get", () => findCase(page, fixture.unrelated));
    await expect(
      page.locator(
        ".case-header-state [data-region], .entries-view [data-entry='policyNumber'], .case-work, .case-timeline-panel",
      ),
    ).toHaveCount(0);
    await expect(page.locator(".case-header-state")).toBeEmpty();
    await expect(page.locator("#case-detail-title")).toHaveText(fixture.unrelated);
  }
};
const cases: ReadonlyArray<readonly [string, NarrowRole | null, boolean]> = [
  ["installation reader", "CASE_READER", false],
  ["case reader", "CASE_READER", true],
  ["case handler", "CASE_HANDLER", true],
  ["steward", "DATA_STEWARD", false],
  ["enabled actor without grants", null, false],
];
const teardown: {
  current: (() => Promise<void>) | null;
} = {
  current: null,
};
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
    if (role === "CASE_READER" && scoped) {
      test.setTimeout(60_000);
    }
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
    if (role === "CASE_READER" && scoped) {
      await narrow.setViewportSize({ width: 320, height: 900 });
      let reads = 0;
      narrow.on("request", (request) => {
        if (new URL(request.url()).pathname === "/api/people.history") {
          reads += 1;
        }
      });
      await narrow.goto("/people-history");
      const navigation = narrow.locator(".workspace-mobile-tabs");
      await expect(navigation.getByRole("link", { name: /^Cases/u })).toBeVisible();
      await navigation.locator(".compact-navigation > summary").press("Enter");
      await expect(navigation.getByRole("link", { name: /^Unconfirmed changes/u })).toBeVisible();
      await expect(navigation.getByRole("link", { name: "Access history" })).toHaveCount(0);
      await expect(
        narrow.getByRole("heading", { name: "Access history", exact: true }),
      ).toBeVisible();
      await expect(narrow.locator("main").getByRole("alert")).toBeVisible();
      expect(reads).toBe(0);
      await narrow.goto("/cases?view=ALL");
      await expect(narrow.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
      await narrow.setViewportSize({ width: 1280, height: 900 });
    }
    if (role === "CASE_READER" || role === "CASE_HANDLER" || role === "DATA_STEWARD") {
      await test.step("Read current case and enforce history scope", () =>
        readCase(narrow, fixture, role, scoped));
    } else {
      await noCaseWorkspace(narrow);
      await expect(
        narrow.locator(
          ".entries-view [data-entry='policyNumber'], .case-work, .case-timeline-panel",
        ),
      ).toHaveCount(0);
      await opaqueCaseRefusal(narrow, fixture.reference);
    }
    await test.step("Check narrow view accessibility", () => expectAccessible(narrow));
  });
}
test.describe("RP-initiated sign-out", () => {
  // The provider navigation and logout reply carry an ID token hint.
  test.use({ trace: "off" });
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
      const sessionReply = reply.payload as ResponseByEndpoint["session"];
      if (sessionReply.outcome.kind !== "SUCCEEDED") {
        throw new Error("E2E_STEWARD_SESSION_RESPONSE_INVALID");
      }
      expect(sessionReply.outcome.data.authenticated).toBe(expected);
    };
    await context.addCookies(revoked);
    await page.goto("/", {
      waitUntil: "commit",
    });
    await expect(page.locator(".login-card h1")).toBeVisible();
    await expectAuthenticated(false);
    const fresh = await authenticateSteward(browser, baseURL);
    await openAuthenticated(page, "Cases", fresh);
    await expectAuthenticated(true);
    await context.addCookies(revoked);
    await page.goto("/", {
      waitUntil: "commit",
    });
    await expect(page.locator(".login-card h1")).toBeVisible();
    await expectAuthenticated(false);
  });
});
