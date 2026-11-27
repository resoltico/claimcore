import { randomUUID } from "node:crypto";
import type { ResponseFor } from "@contracts/client";
import { expect, test, type Page } from "./fixtures";
import { applyPeople, selectedActor, syntheticPrincipals } from "./people-setup";
import { reviewedPeopleBinding } from "./people-revision";
import { login, openAuthenticated, seedSignedOut } from "./session-helpers";
import { casePath } from "../src/routing/routes";
import { operationId } from "../src/operation/operationId";

const reactivate = async (page: Page) => {
  await page.bringToFront();
  const request = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/case.get",
  );
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  return (await (await request).json()) as ResponseFor<"case.get", "HUMAN">;
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
