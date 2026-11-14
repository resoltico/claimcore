import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { type ResponseFor, type RequestByEndpoint } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";

import { operationId } from "../src/operation/operationId";
test("successful summary continuation removes previously loaded full history after permission revocation", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `DISCLOSURE-${randomUUID()}`;
  await openCase(page, reference);
  for (let revision = 1; revision <= 12; revision += 1) {
    await applyPeople(page, {
      operationId: operationId(),
      task: "case.amendRegistration",
      binding: {
        kind: "CASE",
        target: {
          kind: "REFERENCE",
          caseReference: reference,
        },
        expectedRevision: revision,
      },
      inputs: { "field:claimantName": `PRIVATE_HISTORY_${revision}` },
    });
  }
  const principals = await syntheticPrincipals();
  const target = {
    kind: "HUMAN",
    issuer: principals.issuer,
    subject: principals.ownerSubject,
  } as const;
  await applyPeople(page, {
    operationId: operationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
    inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
  });
  try {
    await page.reload();
    const history = page.locator(".case-timeline-panel");
    await expect(history).toContainText("PRIVATE_HISTORY_11");
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.revoke",
      binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
      inputs: { "input:role": "CASE_HANDLER", "input:scope": "INSTALLATION" },
    });
    const refreshed = page.waitForResponse(async (response) => {
      if (new URL(response.url()).pathname !== "/api/case.timeline") {
        return false;
      }
      const caseTimelineRequest = response
        .request()
        .postDataJSON() as RequestByEndpoint["case.timeline"];
      if (caseTimelineRequest.cursor !== undefined) {
        return false;
      }
      const caseTimelineReply = (await response.json()) as ResponseFor<"case.timeline", "HUMAN">;
      return (
        caseTimelineReply.representation === "HUMAN" &&
        caseTimelineReply.outcome.kind === "SUCCEEDED" &&
        caseTimelineReply.outcome.document.disclosure === "SUMMARY"
      );
    });
    const continuation = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.timeline",
    );
    await history
      .getByRole("button", {
        name: "Load more",
        exact: true,
      })
      .click();
    const caseTimelineReply = (await (await continuation).json()) as ResponseFor<
      "case.timeline",
      "HUMAN"
    >;
    if (
      caseTimelineReply.representation !== "HUMAN" ||
      caseTimelineReply.outcome.kind !== "SUCCEEDED"
    ) {
      throw Error("Successful timeline continuation required");
    }
    expect(caseTimelineReply.outcome.document.disclosure).toBe("SUMMARY");
    await refreshed;
    await expect(history).not.toContainText("PRIVATE_HISTORY_11");
    await expect(history).not.toContainText("PRIVATE_HISTORY_12");
    // Tightened continuation invalidates workspace observations and reloads the bounded first page.
    await expect(history.locator(".region-timeline > li")).toHaveCount(10);
  } finally {
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.grant",
      binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
      inputs: { "input:role": "CASE_HANDLER", "input:scope": "INSTALLATION" },
    });
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.revoke",
      binding: { kind: "PEOPLE", actorId: await selectedActor(page, target) },
      inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
    });
  }
});
