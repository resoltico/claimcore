import { reviewedPeopleBinding } from "./people-revision";
import { selectedActor, applyPeople, syntheticPrincipals } from "./people-setup";
import { type ResponseFor, type RequestByEndpoint } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";

import { operationId } from "../src/operation/operationId";
test("successful summary continuation removes previously loaded full history after permission revocation", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `DISCLOSURE-${randomUUID().slice(0, 8)}`;
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
      inputs: { "field:policyNumber": `PRIVATE_HISTORY_${revision}` },
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
    binding: await reviewedPeopleBinding(page, await selectedActor(page, target)),
    inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
  });
  const release = Promise.withResolvers<void>();
  const captured = Promise.withResolvers<string>();
  await page.route("**/api/case.timeline", async (route) => {
    const input = route.request().postDataJSON() as RequestByEndpoint["case.timeline"];
    if (input.filter !== "FACTS") {
      await route.continue();
      return;
    }
    try {
      const reply = await route.fetch();
      captured.resolve(await reply.text());
      await release.promise;
      await route.fulfill({ response: reply }).catch(() => undefined);
    } catch (error) {
      captured.reject(error);
      throw error;
    }
  });
  try {
    await page.reload();
    const history = page.locator(".case-timeline-panel");
    await history.getByRole("button", { name: "View details", exact: true }).first().click();
    const details = page.getByRole("dialog", { name: "View details", exact: true });
    await expect(details).toContainText("PRIVATE_HISTORY_11");
    await details.getByRole("button", { name: "Close", exact: true }).first().click();
    await history.getByRole("tab", { name: "Case facts" }).click();
    expect(await captured.promise).toContain("PRIVATE_HISTORY_11");
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.revoke",
      binding: await reviewedPeopleBinding(page, await selectedActor(page, target)),
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
    await history.getByRole("tab", { name: "Everything" }).click();
    const restricted = await refreshed;
    expect(await restricted.text()).not.toContain("PRIVATE_HISTORY_");
    await expect(history.locator(".region-timeline > li")).toHaveCount(10);
    release.resolve();
    await expect(history.locator(".timeline-comparison")).toHaveCount(0);
    await expect(history).not.toContainText("PRIVATE_HISTORY_11");
    await expect(history).not.toContainText("PRIVATE_HISTORY_12");
  } finally {
    release.resolve();
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.grant",
      binding: await reviewedPeopleBinding(page, await selectedActor(page, target)),
      inputs: { "input:role": "CASE_HANDLER", "input:scope": "INSTALLATION" },
    });
    await applyPeople(page, {
      operationId: operationId(),
      task: "people.revoke",
      binding: await reviewedPeopleBinding(page, await selectedActor(page, target)),
      inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
    });
  }
});
