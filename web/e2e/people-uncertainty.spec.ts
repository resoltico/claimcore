import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { chooseChoice, goToView } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test, type Page, type BrowserContext } from "@playwright/test";

import { openAuthenticated, authenticateSteward, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  pauseJsonReply,
  noHorizontalOverflow,
  ui,
  reviewFrom,
} from "./localization-support";

let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});

const ownerGrant = async (page: Page, active: boolean) => {
  const source = await syntheticPrincipals();
  await applyPeople(page, {
    operationId: mintOperationId(),
    task: active ? "people.grant" : "people.revoke",
    binding: {
      kind: "PEOPLE",
      actorId: await selectedActor(page, {
        kind: "HUMAN",
        issuer: source.issuer,
        subject: source.stewardSubject,
      }),
    },
    inputs: { "input:role": "OWNER", "input:scope": "INSTALLATION" },
  });
};
const prepareAdmission = async (page: Page, issuer: string, subject: string) => {
  await page.getByRole("button", { name: "⟦ÀÀdd pëërsöön⟧", exact: true }).first().click();
  const dialog = page.getByRole("dialog");
  await chooseChoice(page, "input:principal.kind", "SERVICE");
  await expect(dialog.locator('[name="input:principal.issuer"]')).toHaveValue(issuer);
  await expect(dialog.locator('[name="input:principal.issuer"]')).toBeEnabled();
  await dialog.locator('[name="input:principal.subject"]').fill(subject);
  await dialog.locator('[name="input:profile.displayName"]').fill("Authored uncertain service");
  await dialog.locator('input[type="radio"][value="CASE_READER"]').check();
  const pending = await pauseJsonReply(page, "operation.preview");
  try {
    await dialog.locator('button[type="submit"]').click();
    const captured = await pending.ready;
    const identity = reviewFrom(captured.reply, captured.bytes);
    expect(JSON.parse(captured.bytes.toString("utf8"))).toEqual({
      operationId: identity.operationId,
      kind: "PEOPLE",
      target: { kind: "SERVICE", issuer, subject },
      act: {
        kind: "people.admit",
        profile: { displayName: "Authored uncertain service" },
        grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
      },
    });
    return { bytes: captured.bytes, operationId: identity.operationId };
  } finally {
    pending.release();
  }
};
const uncertainAdmission = async (page: Page, prepared: { bytes: Buffer; operationId: string }) => {
  const pending = await pauseJsonReply(page, "operation.execute", true);
  try {
    await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
    const captured = await pending.ready;
    expect(captured.bytes.equals(prepared.bytes)).toBe(true);
    expect(captured.reply.outcome.kind).toBe("ACCEPTED");
    expect(captured.reply.operationId).toBe(prepared.operationId);
  } finally {
    pending.release();
  }
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
};

const refusedRetry = async (page: Page, bytes: Buffer) => {
  const pending = await pauseJsonReply(page, "operation.execute");
  await page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }).click();
  const captured = await pending.ready;
  expect(captured.bytes.equals(bytes)).toBe(true);
  expect(captured.reply.outcome).toMatchObject({
    kind: "REJECTED",
    rejection: {
      kind: "REFUSED",
      advice: {
        action: "ASK_FOR_ACCESS",
        message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE" },
      },
    },
  });
  pending.release();
  await expect(page.locator(".people-rows > tr")).toHaveCount(0);
  // Empty rows also occur during revalidation; wait for the revoked view and its data to disappear.
  await expect(
    page.locator(
      '.workspace-sidebar nav a[href="/people"], .workspace-mobile-tabs a[href="/people"]',
    ),
  ).toHaveCount(0);
  await expect(page.locator(".people-table")).toHaveCount(0);
  const resume = page.getByRole("button", {
    name: ui("en", "ui.continuePeopleRequest"),
    exact: true,
  });
  await expect(resume).toBeEnabled();
  await resume.click();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeEnabled();
  await expect(page.locator(".people-rows > tr")).toHaveCount(0);
};

const cleanupPeople = async (
  page: Page,
  context: BrowserContext | null,
  primary: Error | undefined,
) => {
  const outcomes = await Promise.allSettled([
    ownerGrant(page, false),
    ...(context === null ? [] : [context.close()]),
  ]);
  const failures = outcomes.flatMap((outcome) => {
    if (outcome.status === "fulfilled") {
      return [];
    }
    const reason: unknown = outcome.reason;
    return [reason];
  });
  if (failures.length > 0) {
    test.info().annotations.push({
      type: "cleanup-failure",
      description: `${failures.length} people cleanup actions failed.`,
    });
    await test.info().attach("people-cleanup-failures", {
      body: Buffer.from(
        failures
          .map((failure) =>
            failure instanceof Error ? (failure.stack ?? failure.message) : String(failure),
          )
          .join("\n\n"),
      ),
      contentType: "text/plain",
    });
  }
  if (primary !== undefined) {
    throw primary;
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "People cleanup failed.");
  }
};

test("people review and uncertainty survive locale changes while refusal clears prior actor disclosures", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  let context: BrowserContext | null = null;
  let primary: Error | undefined;
  try {
    await ownerGrant(page, true);
    context = await browser.newContext({
      baseURL: new URL(page.url()).origin,
      ignoreHTTPSErrors: true,
    });
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", stewardCookies);
    const subject = `people-uncertain-${randomUUID()}`;
    await goToView(steward, "people");
    const priorActor = steward.locator(".table-view tbody .actor-identity").first();
    await expect(priorActor).toBeVisible();
    const priorName = (await priorActor.innerText()).trim();
    expect(priorName.length).toBeGreaterThan(0);
    const prepared = await prepareAdmission(steward, source.issuer, subject);
    const writes: string[] = [];
    steward.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/operation.execute") {
        writes.push(request.postData() ?? "");
      }
    });
    await steward.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(steward, "narrow");
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      const review = steward.getByRole("dialog", {
        name: ui(language, "task.people.admit.label"),
        exact: true,
      });
      await expect(review.locator("button[data-confirm-operation]")).toBeEnabled();
      await expect(review.getByRole("checkbox")).toHaveCount(0);
      await expect(review.locator(".screen-details")).toContainText(subject);
      await expect(review.locator(".screen-details")).toContainText(prepared.operationId);
      expect(writes).toEqual([]);
      await noHorizontalOverflow(steward, "narrow");
    }
    await uncertainAdmission(steward, prepared);
    expect(writes).toEqual([prepared.bytes.toString("utf8")]);
    await ownerGrant(page, false);
    await refusedRetry(steward, prepared.bytes);
    expect(writes).toEqual([prepared.bytes.toString("utf8"), prepared.bytes.toString("utf8")]);
    await expect(steward.locator('section[aria-labelledby="people-title"]')).not.toContainText(
      priorName,
    );
    await expect(steward.locator('section[aria-labelledby="people-title"]')).not.toContainText(
      subject,
    );
    await expectAccessible(steward);
  } catch (error) {
    primary =
      error instanceof Error ? error : new Error("People workflow failed.", { cause: error });
  } finally {
    await cleanupPeople(page, context, primary);
  }
});
