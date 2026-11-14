import { selectedActor, applyPeople } from "./people-setup";
import { prefixedInputs } from "../tests/support/submission-inputs";
import { type RequestByEndpoint } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, reviewDraft } from "./case-workflow";
import { workRequest } from "./work-workflow";

import { goToView } from "./workspace-navigation";
import { operationId } from "../src/operation/operationId";
const expectWorklistReady = async (
  page: Page,
  removed: string,
  remaining?: string,
): Promise<void> => {
  const list = page.locator(".worklist");
  const rows = list.locator("tbody tr");
  const retry = page
    .getByRole("alert")
    .filter({
      hasText: "ClaimCore is busy. Try again in a moment.",
    })
    .getByRole("button", {
      name: "Try again",
      exact: true,
    })
    .first();
  const ready = async () =>
    (await list.isVisible()) &&
    (await list.getAttribute("aria-busy")) === "false" &&
    (await list
      .getByRole("tab", {
        selected: true,
      })
      .isVisible()) &&
    (await rows
      .filter({
        hasText: removed,
      })
      .count()) === 0 &&
    (remaining === undefined ||
      (await rows
        .filter({
          hasText: remaining,
        })
        .isVisible()));
  const assertReady = async () => {
    await expect(list).toBeVisible();
    await expect(list).toHaveAttribute("aria-busy", "false");
    await expect(
      list.getByRole("tab", {
        selected: true,
      }),
    ).toBeVisible();
    await expect(
      rows.filter({
        hasText: removed,
      }),
    ).toHaveCount(0);
    if (remaining !== undefined) {
      await expect(
        rows.filter({
          hasText: remaining,
        }),
      ).toBeVisible();
    }
  };
  for (let attempt = 0; attempt < 3; attempt += 1) {
    try {
      await expect.poll(async () => (await retry.isVisible()) || (await ready())).toBe(true);
    } catch (error) {
      await assertReady();
      throw error;
    }
    if (!(await retry.isVisible())) {
      return;
    }
    await retry.click();
  }
  try {
    await expect.poll(ready).toBe(true);
  } catch (error) {
    await assertReady();
    throw error;
  }
};
test("reassigns a filtered list row with its work revision and refuses stale handover @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  // Fixture probes wait for bounded workspace bootstrap reads, as the admission acceptance does.
  await expect(page.locator('.worklist[aria-busy="false"]')).toBeVisible();
  await expect(page.locator('.summary-band[aria-busy="true"]')).toHaveCount(0);
  const context = await workRequest(page, "context", {});
  if (context.outcome.kind !== "SUCCEEDED" || context.outcome.data.requester.kind !== "ACTIVE") {
    throw new Error("Active handler context required");
  }
  const owner = context.outcome.data.requester.actorId;
  const actor = operationId();
  const principal = {
    kind: "HUMAN" as const,
    issuer: context.outcome.data.requester.issuer,
    subject: `handover-${randomUUID()}`,
  };
  await applyPeople(page, {
    operationId: actor,
    task: "people.register",
    binding: { kind: "PEOPLE" },
    inputs: {
      "input:profile.displayName": "Incoming handler",
      "input:profile.contact": null,
      ...prefixedInputs("input:principal.", principal),
    },
  });
  await applyPeople(page, {
    operationId: operationId(),
    task: "people.grant",
    binding: { kind: "PEOPLE", actorId: await selectedActor(page, principal) },
    inputs: { "input:role": "CASE_HANDLER", "input:scope": "INSTALLATION" },
  });
  const references = [`HAND-A-${randomUUID()}`, `HAND-B-${randomUUID()}`] as const;
  for (const reference of references) {
    await goToView(page, "cases");
    await openCase(page, reference);
    const assigned = await workRequest(page, "operation.execute", {
      operationId: operationId(),
      task: "work.update",
      binding: {
        kind: "WORK",
        target: {
          kind: "REFERENCE",
          caseReference: reference,
        },
        expectedRevision: 0,
      },
      inputs: { "field:responsibleActor": owner },
    });
    expect(assigned.outcome.kind).toBe("ACCEPTED");
  }
  await page.goto(`/people/${owner}`);
  const responsibility = page.getByRole("link", {
    name: /^Responsible open cases:/u,
  });
  await expect(responsibility).toBeVisible();
  const link = new URL((await responsibility.getAttribute("href"))!, page.url());
  expect(link.searchParams.get("responsibleActor")).toBe(owner);
  expect(link.searchParams.get("status")).toBe("OPENED");
  expect(link.searchParams.get("lifecycle")).toBe("ACTIVE");
  await responsibility.click();
  const first = page.locator(".worklist tbody tr").filter({
    hasText: references[0],
  });
  await expect(
    first.getByRole("button", {
      name: "Reassign",
      exact: true,
    }),
  ).toBeVisible();
  await expect(first.locator('[data-column="workRevision"]')).toHaveCount(0);
  await expect(first.locator('[data-entry="workRevision"]')).not.toBeVisible();
  let writes = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      writes += 1;
    }
  });
  await first
    .getByRole("button", {
      name: "Reassign",
      exact: true,
    })
    .click();
  await page.locator(`.assignee-input input[type=radio][value="${actor}"]`).check();
  await expect(
    page.getByRole("dialog").getByRole("textbox", {
      name: "Handling summary",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(page.locator('input[data-selects-fields~="handlingSummary"]')).not.toBeChecked();
  const preview = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await reviewDraft(page);
  const reviewed = await preview;
  expect(reviewed.request().postDataJSON()).toMatchObject({
    task: "work.update",
    binding: { kind: "WORK", expectedRevision: 1 },
    inputs: { "field:responsibleActor": actor },
  });
  const operationPreviewRequest = reviewed
    .request()
    .postDataJSON() as RequestByEndpoint["operation.preview"];
  if (operationPreviewRequest.binding.kind !== "WORK") {
    throw new Error("Expected a typed work review");
  }
  expect({ task: operationPreviewRequest.task, inputs: operationPreviewRequest.inputs }).toEqual({
    task: "work.update",
    inputs: {
      "field:responsibleActor": {
        kind: "SET",
        value: actor,
      },
    },
  });
  expect(writes).toBe(0);
  await expectAccessible(page);
  const accepted = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
  );
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  const written = await accepted;
  expect(written.request().postData()).toBe(reviewed.request().postData());
  expect(await written.json()).toMatchObject({
    representation: "HUMAN",
    outcome: {
      kind: "ACCEPTED",
      document: {
        context: {
          target: {
            kind: "WORK",
            revision: 2,
          },
        },
      },
    },
  });
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expectWorklistReady(page, references[0], references[1]);
  await expect(first).toHaveCount(0);
  expect(writes).toBe(1);
  expect(page.url()).toContain(`responsibleActor=${owner}`);
  const second = page.locator(".worklist tbody tr").filter({
    hasText: references[1],
  });
  await expect(
    second.getByRole("button", {
      name: "Reassign",
      exact: true,
    }),
  ).toBeVisible();
  const concurrent = await workRequest(page, "operation.execute", {
    operationId: operationId(),
    task: "work.update",
    binding: {
      kind: "WORK",
      target: {
        kind: "REFERENCE",
        caseReference: references[1],
      },
      expectedRevision: 1,
    },
    inputs: { "field:handlingSummary": "Changed while the row was displayed" },
  });
  expect(concurrent.outcome.kind).toBe("ACCEPTED");
  const writtenBeforeReview = writes;
  await second
    .getByRole("button", {
      name: "Reassign",
      exact: true,
    })
    .click();
  await page.locator(`.assignee-input input[type=radio][value="${actor}"]`).check();
  const stale = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await page
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const refused = await stale;
  expect(refused.request().postDataJSON()).toMatchObject({
    expectedRevision: 1,
  });
  expect(await refused.json()).toMatchObject({
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "CONFLICT",
        actualRevision: 2,
        advice: {
          message: {
            messageId: "diagnostic.REVISION_CONFLICT",
          },
        },
      },
    },
  });
  expect(writes).toBe(writtenBeforeReview);
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toHaveCount(0);
  const latest = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Review against latest",
      exact: true,
    })
    .click();
  const reviewedLatest = await latest;
  expect(reviewedLatest.request().postDataJSON()).toMatchObject({
    expectedRevision: 2,
    act: {
      values: {
        responsibleActor: {
          kind: "SET",
          value: actor,
        },
      },
    },
  });
  const confirm = page.getByRole("dialog").locator("button[data-confirm-operation]");
  await expect(confirm).toBeEnabled();
  expect(writes).toBe(writtenBeforeReview);
  await confirm.click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expectWorklistReady(page, references[1]);
  await expect(second).toHaveCount(0);
  expect(writes).toBe(writtenBeforeReview + 1);
  expect(page.url()).toContain(`responsibleActor=${owner}`);
});
