import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";
import { applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId } from "../src/operation/operationId";
import { isResponseFor, isRequest } from "../src/protocol/client";

test("successful summary continuation removes previously loaded full history after permission revocation", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `DISCLOSURE-${randomUUID()}`;
  await openCase(page, reference);
  for (let revision = 1; revision <= 12; revision += 1) {
    await applyPeople(page, {
      kind: "CASE",
      operationId: operationId(),
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: revision,
      act: {
        kind: "case.amendRegistration",
        values: { claimantName: `PRIVATE_HISTORY_${revision}` },
      },
    });
  }
  const principals = await syntheticPrincipals();
  const target = {
    kind: "HUMAN",
    issuer: principals.issuer,
    subject: principals.ownerSubject,
  } as const;
  await applyPeople(page, {
    kind: "PEOPLE",
    operationId: operationId(),
    target,
    act: { kind: "people.grant", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
  });
  try {
    await page.reload();
    const history = page.locator(".case-timeline-panel");
    await expect(history).toContainText("PRIVATE_HISTORY_11");
    await applyPeople(page, {
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: { kind: "people.revoke", role: "CASE_HANDLER", scope: { kind: "INSTALLATION" } },
    });
    const refreshed = page.waitForResponse(async (response) => {
      if (new URL(response.url()).pathname !== "/api/case.timeline") {
        return false;
      }
      const input: unknown = response.request().postDataJSON();
      if (!isRequest("case.timeline", input)) {
        throw new Error("Expected typed history request");
      }
      if (input.cursor !== undefined) {
        return false;
      }
      const value: unknown = await response.json();
      return (
        isResponseFor("case.timeline", "HUMAN", value) &&
        value.outcome.kind === "SUCCEEDED" &&
        value.outcome.document.disclosure === "SUMMARY"
      );
    });
    const continuation = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.timeline",
    );
    await history.getByRole("button", { name: "Load more", exact: true }).click();
    const payload: unknown = await (await continuation).json();
    if (!isResponseFor("case.timeline", "HUMAN", payload) || payload.outcome.kind !== "SUCCEEDED") {
      throw Error("Successful timeline continuation required");
    }
    expect(payload.outcome.document.disclosure).toBe("SUMMARY");
    await refreshed;
    await expect(history).not.toContainText("PRIVATE_HISTORY_11");
    await expect(history).not.toContainText("PRIVATE_HISTORY_12");
    // Tightened continuation invalidates workspace observations and reloads the bounded first page.
    await expect(history.locator(".region-timeline > li")).toHaveCount(10);
  } finally {
    await applyPeople(page, {
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: { kind: "people.grant", role: "CASE_HANDLER", scope: { kind: "INSTALLATION" } },
    });
    await applyPeople(page, {
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: { kind: "people.revoke", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
    });
  }
});
