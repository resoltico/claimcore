import { randomUUID } from "node:crypto";
import type { ResponseFor } from "@contracts/client";
import { expect, test, type Page } from "./fixtures";
import { applyPeople, selectedActor, syntheticPrincipals } from "./people-setup";
import { reviewedPeopleBinding } from "./people-revision";
import { retainResponseBodies } from "./response-bodies";
import { authenticateSteward, login, openAuthenticated, seedSignedOut } from "./session-helpers";
import { casePath } from "../src/routing/routes";
import { operationId } from "../src/operation/operationId";
import { workRequest } from "./work-workflow";

const reactivate = async (page: Page) => {
  await page.bringToFront();
  const received = page
    .waitForResponse((response) => new URL(response.url()).pathname === "/api/case.get")
    .then(async (response) => (await response.json()) as ResponseFor<"case.get", "HUMAN">);
  const [reply] = await Promise.all([
    received,
    page.evaluate(() => window.dispatchEvent(new Event("focus"))),
  ]);
  return reply;
};

test("separate browser sessions lose restricted related facts on activation @engine", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(150_000);
  const independent = await browser.newContext({
    baseURL: baseURL!,
    ignoreHTTPSErrors: true,
    storageState: { cookies: [], origins: [] },
  });
  const marker = randomUUID().slice(0, 8);
  const sourceFact = `PRIVATE-SOURCE-${marker}`;
  const targetFact = `PRIVATE-TARGET-${marker}`;
  const relation = `PRIVATE-RELATION-${marker}`;
  try {
    const operator = await independent.newPage();
    await seedSignedOut(operator);
    await operator.goto("/", { waitUntil: "commit" });
    await login(operator);
    const open = async (fact: string) => {
      const caseId = operationId();
      await applyPeople(operator, {
        operationId: caseId,
        task: "case.open",
        binding: { kind: "CASE", expectedRevision: 0 },
        inputs: { "field:incidentDescription": fact },
      });
      return caseId;
    };
    const sourceId = await open(sourceFact);
    const targetId = await open(targetFact);
    const otherId = await open(`PRIVATE-SCOPE-CONTROL-${marker}`);
    await applyPeople(operator, {
      operationId: operationId(),
      task: "link.add",
      binding: { kind: "LINK", target: { kind: "ID", caseId: sourceId } },
      inputs: { "input:relatedCaseId": targetId, "input:linkNote": relation },
    });
    const { issuer, ownerSubject } = await syntheticPrincipals();
    const owner = await selectedActor(operator, { kind: "HUMAN", issuer, subject: ownerSubject });
    const change = async (
      task: "people.grant" | "people.revoke",
      role: "CASE_READER" | "CASE_HANDLER" | "PAYMENT_RECORDER",
      caseId?: string,
    ) =>
      applyPeople(operator, {
        operationId: operationId(),
        task,
        binding: await reviewedPeopleBinding(operator, owner),
        inputs: {
          "input:role": role,
          "input:scope": caseId === undefined ? "INSTALLATION" : "CASE",
          ...(caseId === undefined ? {} : { "input:caseId": caseId }),
        },
      });
    await change("people.grant", "CASE_READER", sourceId);
    await change("people.grant", "CASE_READER", targetId);
    await change("people.grant", "CASE_READER", otherId);
    await change("people.revoke", "CASE_HANDLER");
    await change("people.revoke", "PAYMENT_RECORDER");
    try {
      await openAuthenticated(page);
      await page.goto(casePath(sourceId));
      await expect(page.locator(".case-workspace")).toContainText(sourceFact);
      await expect(page.locator(".case-workspace")).toContainText(relation);
      const target = await page.context().newPage();
      await retainResponseBodies(target);
      await target.goto(casePath(targetId));
      await expect(target.locator(".case-workspace")).toContainText(targetFact);

      await operator.bringToFront();
      await change("people.revoke", "CASE_READER", targetId);
      expect((await reactivate(page)).outcome.kind).toBe("SUCCEEDED");
      await expect(page.locator(".case-workspace")).toContainText(sourceFact);
      await expect(page.locator("body")).not.toContainText(relation);
      expect((await reactivate(target)).outcome.kind).toBe("REJECTED");
      await expect(target.locator("body")).not.toContainText(targetFact);

      await operator.bringToFront();
      await change("people.revoke", "CASE_READER", sourceId);
      expect((await reactivate(page)).outcome.kind).toBe("REJECTED");
      await expect(page.locator("body")).not.toContainText(sourceFact);
      await expect(page.locator("body")).not.toContainText(relation);
    } finally {
      await operator.bringToFront();
      await change("people.grant", "CASE_HANDLER");
      await change("people.grant", "PAYMENT_RECORDER");
      await change("people.revoke", "CASE_READER", otherId);
    }
  } finally {
    await independent.close();
  }
});

test("peer erasure removes relationship details from an unchanged source on activation @engine", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(150_000);
  await openAuthenticated(page);
  const sourceId = operationId();
  const peerId = operationId();
  const linkId = operationId();
  const source = { kind: "ID", caseId: sourceId } as const;
  const peer = { kind: "ID", caseId: peerId } as const;
  const marker = randomUUID().slice(0, 8);
  const sourceFact = `ERASURE-SOURCE-${marker}`;
  const privateNote = `ERASURE-LINK-${marker}`;

  for (const [caseId, fact] of [
    [sourceId, sourceFact],
    [peerId, `ERASURE-PEER-${marker}`],
  ] as const) {
    await applyPeople(page, {
      operationId: caseId,
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: { "field:incidentDescription": fact },
    });
  }
  await applyPeople(page, {
    operationId: linkId,
    task: "link.add",
    binding: { kind: "LINK", target: source },
    inputs: { "input:relatedCaseId": peerId, "input:linkNote": privateNote },
  });
  await page.goto(casePath(sourceId));
  const workspace = page.locator(".case-workspace");
  await expect(workspace).toContainText(privateNote);

  const before = await workRequest(page, "case.get", { target: source });
  if (before.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected an authorized source observation.");
  }
  const oldReceipt = await workRequest(page, "operation.get", { operationId: linkId });
  expect(oldReceipt.outcome.kind).toBe("ACCEPTED");
  expect(JSON.stringify(oldReceipt)).toContain(linkId);

  const cookies = await authenticateSteward(browser, baseURL);
  const separate = await browser.newContext({ baseURL: baseURL!, ignoreHTTPSErrors: true });
  try {
    const steward = await separate.newPage();
    await openAuthenticated(steward, "Cases", cookies);
    await steward.bringToFront();
    const reviewed = await workRequest(steward, "case.get", { target: peer });
    if (
      reviewed.outcome.kind !== "SUCCEEDED" ||
      reviewed.outcome.data.erasureReviewToken === undefined
    ) {
      throw new Error("Expected an authorized erasure review.");
    }
    await applyPeople(steward, {
      operationId: operationId(),
      task: "case.erase",
      binding: {
        kind: "CASE",
        target: peer,
        expectedRevision: reviewed.outcome.data.revision,
        erasureReviewToken: reviewed.outcome.data.erasureReviewToken,
      },
      inputs: {},
    });

    const current = await reactivate(page);
    if (current.outcome.kind !== "SUCCEEDED" || current.outcome.document.subject.kind !== "CASE") {
      throw new Error("Expected the unchanged source to remain readable.");
    }
    expect(current.outcome.document.subject.revision).toBe(before.outcome.data.revision);
    await expect(workspace).toContainText(sourceFact);
    await expect(page.locator("body")).not.toContainText(privateNote);
    const history = await workRequest(page, "case.timeline", { target: source });
    expect(history.outcome.kind).toBe("SUCCEEDED");
    expect(JSON.stringify(history)).toContain(linkId);
    expect(JSON.stringify(history)).not.toContain(privateNote);
    const receipt = await workRequest(page, "operation.get", { operationId: linkId });
    expect(receipt.outcome.kind).toBe("ACCEPTED");
    expect(JSON.stringify(receipt)).toContain(linkId);
    expect(JSON.stringify(receipt)).not.toContain(privateNote);
    expect(JSON.stringify(receipt)).not.toContain(peerId);
  } finally {
    await separate.close();
  }
});
