import { reviewedPeopleBinding } from "./people-revision";
import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { expect, test, type Page } from "@playwright/test";

import { configureRole, prepareRoleFixture } from "./narrow-role-fixture";
import { noCaseWorkspace, opaqueCaseRefusal } from "./narrow-role-oracles";
import { browserRequest, login, sessionToken } from "./session-helpers";
import { type ResponseFor, endpoints } from "@contracts/client";

const ownerRoles = ["CASE_HANDLER", "PAYMENT_RECORDER"] as const;
const ownerGrants = async (page: Page, active: boolean) => {
  const source = await syntheticPrincipals();
  const changed: (typeof ownerRoles)[number][] = [];
  try {
    for (const role of ownerRoles) {
      await applyPeople(page, {
        operationId: mintOperationId(),
        task: active ? "people.grant" : "people.revoke",
        binding: await reviewedPeopleBinding(
          page,
          await selectedActor(page, {
            kind: "HUMAN",
            issuer: source.issuer,
            subject: source.ownerSubject,
          }),
        ),
        inputs: { "input:role": role, "input:scope": "INSTALLATION" },
      });
      changed.push(role);
    }
  } catch (error) {
    if (!active) {
      for (const role of changed.reverse()) {
        await applyPeople(page, {
          operationId: mintOperationId(),
          task: "people.grant",
          binding: await reviewedPeopleBinding(
            page,
            await selectedActor(page, {
              kind: "HUMAN",
              issuer: source.issuer,
              subject: source.ownerSubject,
            }),
          ),
          inputs: { "input:role": role, "input:scope": "INSTALLATION" },
        });
      }
    }
    throw error;
  }
};

test("owner-only workspace renders without inventing case people", async ({ page }) => {
  const fixture = await prepareRoleFixture(page);
  await ownerGrants(page, false);
  try {
    await page.goto("/");
    await expect(
      page.getByRole("heading", { name: "People and access", exact: true }),
    ).toBeVisible();
    await noCaseWorkspace(page);
    await opaqueCaseRefusal(page, fixture.reference);
  } finally {
    await ownerGrants(page, true);
  }
});

test("disabled actor keeps authenticated public bootstrap but cannot read cases", async ({
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
  const enable = async (enabled: boolean) =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: enabled ? "people.enable" : "people.disable",
      binding: await reviewedPeopleBinding(page, await selectedActor(page, fixture.principal)),
      inputs: {},
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
    await login(disabled, "This account is disabled", "Sign in", "synthetic-steward");
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

test("unregistered human authenticates without gaining a wider grant", async ({
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
    await login(unknown, "You don’t have access yet", "Sign in", "synthetic-outsider");
    expect((await sessionToken(unknown)).length).toBeGreaterThan(0);
    await noCaseWorkspace(unknown);
    await opaqueCaseRefusal(unknown, fixture.reference);
  } finally {
    await context.close();
  }
});

test("changing sign-in identity moves case access to a fresh session", async ({
  page,
  browser,
}) => {
  test.setTimeout(2 * freshLoginWorkflowTimeout);
  const fixture = await prepareRoleFixture(page);
  const inventory = await syntheticPrincipals();
  const actor = await selectedActor(page, fixture.principal);
  const { origin } = new URL(page.url());
  const existing = await browser.newContext({ baseURL: origin, ignoreHTTPSErrors: true });
  const replacement = await browser.newContext({ baseURL: origin, ignoreHTTPSErrors: true });
  const bind = async (subject: string) =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: "people.changeSignInIdentity",
      binding: await reviewedPeopleBinding(page, actor),
      inputs: {
        "input:principal.issuer": inventory.issuer,
        "input:principal.subject": subject,
      },
    });
  const verifyAccess = async (viewer: Page) => {
    const endpoint = endpoints.find((entry) => entry.id === "case.get")!;
    const response = await browserRequest(viewer, endpoint.path, {
      method: endpoint.method,
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(viewer),
      },
      body: JSON.stringify({ target: { kind: "REFERENCE", caseReference: fixture.reference } }),
    });
    expect(response.status).toBe(200);
    const { outcome } = response.payload as ResponseFor<"case.get", "DATA">;
    expect(outcome.kind).toBe("SUCCEEDED");
  };
  let moved = false;
  try {
    const oldSession = await existing.newPage();
    await oldSession.goto("/");
    await login(oldSession, "Cases", "Sign in", "synthetic-steward");
    await verifyAccess(oldSession);

    await bind(inventory.replacementSubject);
    moved = true;
    await oldSession.reload();
    await expect(
      oldSession.getByRole("heading", { name: "You don’t have access yet" }),
    ).toBeVisible();
    await opaqueCaseRefusal(oldSession, fixture.reference);

    const newSession = await replacement.newPage();
    await newSession.goto("/");
    await login(newSession, "Cases", "Sign in", "synthetic-replacement");
    await verifyAccess(newSession);
    expect(
      await selectedActor(page, { ...fixture.principal, subject: inventory.replacementSubject }),
    ).toBe(actor);
  } finally {
    try {
      if (moved) {
        await bind(inventory.stewardSubject);
      }
    } finally {
      await Promise.all([existing.close(), replacement.close()]);
    }
  }
});
