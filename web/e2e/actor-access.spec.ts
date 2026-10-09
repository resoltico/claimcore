import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { configureRole, prepareRoleFixture } from "./narrow-role-fixture";
import { refusedRead } from "./narrow-role-oracles";
import { login, sessionToken } from "./session-helpers";

const ownerRoles = ["CASE_EDITOR", "RECOVERY_OPERATOR", "RECOVERY_EXPORTER"] as const;
const ownerGrants = async (page: Page, active: boolean) => {
  const source = await syntheticPrincipals();
  const changed: (typeof ownerRoles)[number][] = [];
  try {
    for (const role of ownerRoles) {
      await applyAuthority(page, "authority.setGrant", {
        eventId: randomUUID(),
        principal: { kind: "HUMAN", issuer: source.issuer, subject: source.ownerSubject },
        role,
        scope: { kind: "INSTALLATION" },
        active,
      });
      changed.push(role);
    }
  } catch (error) {
    if (!active) {
      for (const role of changed.reverse()) {
        await applyAuthority(page, "authority.setGrant", {
          eventId: randomUUID(),
          principal: { kind: "HUMAN", issuer: source.issuer, subject: source.ownerSubject },
          role,
          scope: { kind: "INSTALLATION" },
          active: true,
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
    await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
    await page.getByLabel("Handler's case reference (exact)").fill(fixture.reference);
    await refusedRead(page, "case.get", () =>
      page.getByRole("button", { name: "Find case", exact: true }).click(),
    );
    await expect(page.getByRole("region", { name: "current case", exact: true })).toHaveCount(0);
  } finally {
    await ownerGrants(page, true);
  }
});

test("disabled actor keeps authenticated public bootstrap but cannot read cases [CC-AUTH-001]", async ({
  page,
  browser,
}) => {
  const fixture = await prepareRoleFixture(page);
  let restore: () => Promise<void> = () => Promise.resolve();
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  const enable = (enabled: boolean) =>
    applyAuthority(page, "authority.setEnabled", {
      eventId: randomUUID(),
      principal: fixture.principal,
      enabled,
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
    await login(disabled, "Cases", "Sign in", "synthetic-steward");
    expect((await sessionToken(disabled)).length).toBeGreaterThan(0);
    await disabled.getByLabel("Handler's case reference (exact)").fill(fixture.reference);
    await refusedRead(disabled, "case.get", () =>
      disabled.getByRole("button", { name: "Find case", exact: true }).click(),
    );
    await expect(disabled.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
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
  const fixture = await prepareRoleFixture(page);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const unknown = await context.newPage();
    await unknown.goto("/");
    await login(unknown, "Cases", "Sign in", "synthetic-outsider");
    expect((await sessionToken(unknown)).length).toBeGreaterThan(0);
    await unknown.getByLabel("Handler's case reference (exact)").fill(fixture.reference);
    await refusedRead(unknown, "case.get", () =>
      unknown.getByRole("button", { name: "Find case", exact: true }).click(),
    );
    await expect(unknown.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
  } finally {
    await context.close();
  }
});
