import { reviewedPeopleBinding } from "./people-revision";
import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { expect, test, type Page } from "./fixtures";

import { configureRole, prepareRoleFixture } from "./narrow-role-fixture";
import { noCaseWorkspace, opaqueCaseRefusal } from "./narrow-role-oracles";
import {
  browserRequest,
  expectHostFailure,
  installationId,
  login,
  sessionToken,
} from "./session-helpers";
import { type RequestByEndpoint, type ResponseFor } from "@contracts/client";

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
    await login(unknown, "Request access", "Sign in", "synthetic-outsider");
    expect((await sessionToken(unknown)).length).toBeGreaterThan(0);
    await noCaseWorkspace(unknown);
    await opaqueCaseRefusal(unknown, fixture.reference);
  } finally {
    await context.close();
  }
});

test("revocation, disablement and rebinding retire only the affected authority", async ({
  page,
  browser,
}) => {
  test.setTimeout(5 * freshLoginWorkflowTimeout);
  const fixture = await prepareRoleFixture(page);
  const accounts = await syntheticPrincipals();
  const actorId = await selectedActor(page, fixture.principal);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  const target = { kind: "REFERENCE", caseReference: fixture.reference } as const;
  const register = await installationId(page, await sessionToken(page));
  const post = async (client: Page, path: string, body: unknown, bind = false) => {
    const antiforgery = await sessionToken(client);
    return browserRequest(client, path, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": antiforgery,
        ...(bind ? { "ClaimCore-Installation": register } : {}),
      },
      body: JSON.stringify(body),
    });
  };
  const read = (client: Page) => post(client, "/api/case.get", { target });
  const expectCase = async (client: Page) => {
    const reply = await read(client);
    expect(reply.status).toBe(200);
    const { outcome } = reply.payload as ResponseFor<"case.get", "DATA">;
    if (outcome.kind !== "SUCCEEDED") {
      throw new Error("Synthetic case must remain readable.");
    }
    return outcome.data;
  };
  const setEnabled = async (enabled: boolean) =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: enabled ? "people.enable" : "people.disable",
      binding: await reviewedPeopleBinding(page, actorId),
      inputs: {},
    });
  const rebind = async (subject: string) =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: "people.changeSignInIdentity",
      binding: await reviewedPeopleBinding(page, actorId),
      inputs: { "input:principal.issuer": accounts.issuer, "input:principal.subject": subject },
    });
  const grant = async (active: boolean, role: "CASE_READER" | "CASE_HANDLER") =>
    applyPeople(page, {
      operationId: mintOperationId(),
      task: active ? "people.grant" : "people.revoke",
      binding: await reviewedPeopleBinding(page, actorId),
      inputs: {
        "input:role": role,
        "input:scope": "CASE",
        "field:caseReference": fixture.reference,
      },
    });
  let restoreGrants: () => Promise<void> = () => Promise.resolve();
  let readerGranted = false;
  let handlerRevoked = false;
  let disabled = false;
  let rebound = false;
  const member = await context.newPage();
  const signIn = async (heading: string) => {
    if (heading === "Cases") {
      // Clear the whole-second precision and maximum provider lead after a hard cut.
      await member.waitForTimeout(3_100);
    }
    await context.clearCookies();
    await member.goto("/");
    await login(member, heading, "Sign in", "synthetic-steward");
  };

  try {
    await configureRole(page, fixture, "CASE_HANDLER", true, (restore) => {
      restoreGrants = restore;
    });
    await signIn("Cases");
    const original = await expectCase(member);
    const submission: RequestByEndpoint["operation.execute"] = {
      operationId: mintOperationId(),
      task: "case.close",
      binding: { kind: "CASE", target, expectedRevision: original.revision },
      inputs: { "field:outcome": "OTHER" },
    };
    const review = await post(member, "/api/operation.preview", submission, true);
    expect(review.payload).toMatchObject({ outcome: { kind: "REVIEWED" } });

    await grant(true, "CASE_READER");
    readerGranted = true;
    await grant(false, "CASE_HANDLER");
    handlerRevoked = true;
    await expectCase(member);
    const rejected = await post(member, "/api/operation.execute", submission, true);
    expect(rejected.payload).toMatchObject({
      outcome: { kind: "REJECTED", rejection: { code: "NOT_AUTHORIZED" } },
    });
    expect((await expectCase(page)).revision).toBe(original.revision);

    const originalCookies = await context.cookies();
    await setEnabled(false);
    disabled = true;
    await signIn("This account is disabled");
    const statusCookies = await context.cookies();
    await opaqueCaseRefusal(member, fixture.reference);
    await setEnabled(true);
    disabled = false;
    const duringDisabled = await post(member, "/api/context", {});
    expect(duringDisabled.payload).toMatchObject({
      outcome: { data: { requester: { kind: "SIGN_IN_REQUIRED" } } },
    });
    await opaqueCaseRefusal(member, fixture.reference);
    await context.addCookies(originalCookies);
    expectHostFailure(await read(member), 401, "SESSION_REJECTED");
    await signIn("Cases");
    await expectCase(member);

    const protectedCookies = await context.cookies();
    await context.clearCookies();
    await context.addCookies(statusCookies);
    await member.reload();
    await expect(member.getByRole("heading", { name: "Sign in again", exact: true })).toBeVisible();
    await expect(
      member.getByRole("button", { name: "End my ClaimCore browser sessions" }),
    ).toHaveCount(0);
    await expect(member.getByRole("button", { name: "Sign out", exact: true })).toBeVisible();
    const refusedTermination = await post(
      member,
      "/api/session.terminateOwn",
      {
        "input:operationId": mintOperationId(),
      },
      true,
    );
    expect(refusedTermination.status).toBe(200);
    expect(refusedTermination.payload).toMatchObject({
      outcome: { kind: "REJECTED", rejection: { code: "RESOURCE_UNAVAILABLE" } },
    });
    await context.clearCookies();
    await context.addCookies(protectedCookies);
    await expectCase(member);

    await rebind(accounts.replacementSubject);
    rebound = true;
    await signIn("Sign-in identity replaced");
    const former = await post(member, "/api/context", {});
    expect((former.payload as ResponseFor<"context", "DATA">).outcome).toMatchObject({
      kind: "SUCCEEDED",
      data: { requester: { kind: "FORMER_IDENTITY" } },
    });
    expect(JSON.stringify(former.payload)).not.toContain(actorId);
    const cannotRequest = await post(member, "/api/people.requests.submit", {});
    expect(cannotRequest.payload).toMatchObject({
      outcome: { kind: "REJECTED", rejection: { code: "ACTOR_ALREADY_REGISTERED" } },
    });
    const inbox = (await post(page, "/api/people.requests", { limit: 50 })).payload as ResponseFor<
      "people.requests",
      "DATA"
    >;
    if (inbox.outcome.kind !== "SUCCEEDED") {
      throw new Error("Owner inbox did not open.");
    }
    expect(
      inbox.outcome.data.items.some((item) => item.principal.subject === accounts.stewardSubject),
    ).toBe(false);

    const termination = {
      "input:actorId": actorId,
      "input:operationId": mintOperationId(),
    };
    const ended = await post(page, "/api/people.terminateSessions", termination, true);
    const confirmed = (ended.payload as ResponseFor<"people.terminateSessions", "DATA">).outcome;
    expect(confirmed).toMatchObject({ kind: "SUCCEEDED", data: { self: false } });
    if (confirmed.kind !== "SUCCEEDED") {
      throw new Error("Owner termination was not confirmed.");
    }
    expect(confirmed.data.count).toBeGreaterThan(0);
    expectHostFailure(await read(member), 401, "SESSION_REJECTED");

    await rebind(accounts.stewardSubject);
    rebound = false;
    await signIn("Cases");
    await expectCase(member);
    const replay = await post(page, "/api/people.terminateSessions", termination, true);
    expect((replay.payload as ResponseFor<"people.terminateSessions", "DATA">).outcome).toEqual(
      confirmed,
    );
    expect((await expectCase(member)).revision).toBe(original.revision);
    expect(await selectedActor(page, fixture.principal)).toBe(actorId);
  } finally {
    try {
      if (rebound) {
        await rebind(accounts.stewardSubject);
      }
      if (disabled) {
        await setEnabled(true);
      }
      if (readerGranted) {
        await grant(false, "CASE_READER");
      }
      if (handlerRevoked) {
        await grant(true, "CASE_HANDLER");
      }
      await restoreGrants();
    } finally {
      await context.close();
    }
  }
});
