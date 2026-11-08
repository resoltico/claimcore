import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { operationId } from "../src/operation/operationId";
import { isResponse } from "../src/protocol/client";

test("successful summary continuation removes previously loaded full history after permission revocation", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `DISCLOSURE-${randomUUID()}`;
  await openCase(page, reference);
  for (let revision = 1; revision <= 12; revision += 1) {
    await applyAuthority(page, {
      kind: "CASE",
      operationId: operationId(),
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: revision,
      act: { kind: "AMEND_REGISTRATION", values: { claimantName: `PRIVATE_HISTORY_${revision}` } },
    });
  }
  const principals = await syntheticPrincipals();
  const target = {
    kind: "HUMAN",
    issuer: principals.issuer,
    subject: principals.ownerSubject,
  } as const;
  await applyAuthority(page, {
    kind: "AUTHORITY",
    operationId: operationId(),
    target,
    act: { kind: "GRANT", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
  });
  try {
    await page.reload();
    const history = page.locator(".case-timeline-panel");
    await expect(history).toContainText("PRIVATE_HISTORY_11");
    await applyAuthority(page, {
      kind: "AUTHORITY",
      operationId: operationId(),
      target,
      act: { kind: "REVOKE", role: "CASE_HANDLER", scope: { kind: "INSTALLATION" } },
    });
    const continuation = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.timeline",
    );
    await history.getByRole("button", { name: "Load more", exact: true }).click();
    const payload: unknown = await (await continuation).json();
    if (!isResponse("case.timeline", payload) || payload.outcome.kind !== "SUCCEEDED") {
      throw Error("Successful timeline continuation required");
    }
    expect(payload.outcome.data.disclosure).toBe("SUMMARY");
    await expect(history).not.toContainText("PRIVATE_HISTORY_11");
    await expect(history).not.toContainText("PRIVATE_HISTORY_12");
    await expect(history.locator("tbody > tr")).toHaveCount(3);
  } finally {
    await applyAuthority(page, {
      kind: "AUTHORITY",
      operationId: operationId(),
      target,
      act: { kind: "GRANT", role: "CASE_HANDLER", scope: { kind: "INSTALLATION" } },
    });
    await applyAuthority(page, {
      kind: "AUTHORITY",
      operationId: operationId(),
      target,
      act: { kind: "REVOKE", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
    });
  }
});
