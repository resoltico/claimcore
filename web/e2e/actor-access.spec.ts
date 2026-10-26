import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { configureRole, prepareRoleFixture } from "./narrow-role-fixture";
import { noCaseWorkspace, opaqueCaseRefusal } from "./narrow-role-oracles";
import { login, sessionToken } from "./session-helpers";

const ownerRoles = ["CASE_HANDLER", "PAYMENT_RECORDER"] as const;
const ownerGrants = async (page: Page, active: boolean) => {
  const source = await syntheticPrincipals();
  const changed: (typeof ownerRoles)[number][] = [];
  try {
    for (const role of ownerRoles) {
      await applyAuthority(page, {
        kind: "AUTHORITY",
        operationId: randomUUID(),
        target: { kind: "HUMAN", issuer: source.issuer, subject: source.ownerSubject },
        act: { kind: active ? "GRANT" : "REVOKE", role, scope: { kind: "INSTALLATION" } },
      });
      changed.push(role);
    }
  } catch (error) {
    if (!active) {
      for (const role of changed.reverse()) {
        await applyAuthority(page, {
          kind: "AUTHORITY",
          operationId: randomUUID(),
          target: { kind: "HUMAN", issuer: source.issuer, subject: source.ownerSubject },
          act: { kind: "GRANT", role, scope: { kind: "INSTALLATION" } },
        });
      }
    }
    throw error;
  }
};

test("owner-only workspace renders without inventing case authority [CC-AUTH-001]", async ({
  page,
}) => {
  const fixture = await prepareRoleFixture(page);
  await ownerGrants(page, false);
  try {
    await page.reload();
    await expect(page.getByRole("heading", { name: "Authority", exact: true })).toBeVisible();
    await noCaseWorkspace(page);
    await opaqueCaseRefusal(page, fixture.reference);
  } finally {
    await ownerGrants(page, true);
  }
});

test("disabled actor keeps authenticated public bootstrap but cannot read cases [CC-AUTH-001]", async ({
  page,
  browser,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  const fixture = await prepareRoleFixture(page);
  let restore: () => Promise<void> = () => Promise.resolve();
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  const enable = (enabled: boolean) =>
    applyAuthority(page, {
      kind: "AUTHORITY",
      operationId: randomUUID(),
      target: fixture.principal,
      act: { kind: enabled ? "ENABLE" : "DISABLE" },
    });
  let disabledConfirmed = false;
  try {
    await configureRole(page, fixture, "CASE_READER", false, (cleanup) => {
      restore = cleanup;
    });
    await enable(false);
    disabledConfirmed = true;
    const disabled = await context.newPage();
    await disabled.goto("/");
    await login(disabled, null, "Sign in", "synthetic-steward");
    expect((await sessionToken(disabled)).length).toBeGreaterThan(0);
    await noCaseWorkspace(disabled);
    await opaqueCaseRefusal(disabled, fixture.reference);
  } finally {
    try {
      if (disabledConfirmed) {
        await enable(true);
      }
    } finally {
      try {
        await restore();
      } finally {
        await context.close();
      }
    }
  }
});

test("unregistered human authenticates without gaining a wider grant [CC-AUTH-001]", async ({
  page,
  browser,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  const fixture = await prepareRoleFixture(page);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const unknown = await context.newPage();
    await unknown.goto("/");
    await login(unknown, null, "Sign in", "synthetic-outsider");
    expect((await sessionToken(unknown)).length).toBeGreaterThan(0);
    await noCaseWorkspace(unknown);
    await opaqueCaseRefusal(unknown, fixture.reference);
  } finally {
    await context.close();
  }
});
