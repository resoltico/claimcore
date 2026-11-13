import { type ResponseFor, type RequestByEndpoint } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";
import { applyPeople, syntheticPrincipals } from "./people-setup";
import { operationId } from "../src/operation/operationId";
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
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: revision,
      act: {
        kind: "case.amendRegistration",
        values: {
          claimantName: `PRIVATE_HISTORY_${revision}`,
        },
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
    act: {
      kind: "people.grant",
      role: "CASE_READER",
      scope: {
        kind: "INSTALLATION",
      },
    },
  });
  try {
    await page.reload();
    const history = page.locator(".case-timeline-panel");
    await expect(history).toContainText("PRIVATE_HISTORY_11");
    await applyPeople(page, {
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: {
        kind: "people.revoke",
        role: "CASE_HANDLER",
        scope: {
          kind: "INSTALLATION",
        },
      },
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
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: {
        kind: "people.grant",
        role: "CASE_HANDLER",
        scope: {
          kind: "INSTALLATION",
        },
      },
    });
    await applyPeople(page, {
      kind: "PEOPLE",
      operationId: operationId(),
      target,
      act: {
        kind: "people.revoke",
        role: "CASE_READER",
        scope: {
          kind: "INSTALLATION",
        },
      },
    });
  }
});
