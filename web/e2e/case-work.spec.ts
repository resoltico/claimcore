import { recordWorkReview, workRequest } from "./work-workflow";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, recordReviewed, reviewDraft, startAct } from "./case-workflow";
import {
  browserRequest,
  expectAccessible,
  openAuthenticated,
  sessionToken,
} from "./session-helpers";
import { chooseChoice, goToView } from "./workspace-navigation";
import { fillBusinessField } from "./typed-entry";
import { isResponse } from "../src/protocol/client";
import { currentCase } from "./localization-correction-workflow";
import { pauseJsonReply, selectFormat, settingsNavigation } from "./localization-support";

test("records future planned work and scoped responsibility, then finds the case in Mine", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `WORK-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: "{}",
  });
  if (
    !isResponse("context", context.payload) ||
    context.payload.outcome.kind !== "SUCCEEDED" ||
    context.payload.outcome.data.requester.kind !== "ACTIVE"
  ) {
    throw new Error("Work context unavailable");
  }
  const { actorId, businessDate } = context.payload.outcome.data.requester;
  const tomorrow = new Date(`${businessDate}T12:00:00Z`);
  tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
  const planned = tomorrow.toISOString().slice(0, 10);
  const beforeResources = await page.evaluate(
    () => window.performance.getEntriesByType("resource").length,
  );
  const started = performance.now();
  await page.locator("#case-work-update").click();
  await expect(
    page.getByRole("dialog").locator('input[name="act.values.responsibleActor.kind"][value="SET"]'),
  ).toBeVisible();
  const renderMilliseconds = performance.now() - started;
  await page.waitForLoadState("networkidle");
  const resources = await page.evaluate(
    (before) =>
      window.performance
        .getEntriesByType("resource")
        .slice(before)
        .filter(
          (entry): entry is PerformanceResourceTiming => entry instanceof PerformanceResourceTiming,
        )
        .filter((entry) => new URL(entry.name).pathname.startsWith("/assets/"))
        .map((entry) => ({
          path: new URL(entry.name).pathname,
          transfer: entry.transferSize,
          encoded: entry.encodedBodySize,
          decoded: entry.decodedBodySize,
          duration: entry.duration,
        })),
    beforeResources,
  );
  const output = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (output === undefined) {
    throw new Error("Owned new-feature measurement output is required");
  }
  await writeFile(
    resolve(output, "loading-new-work.json"),
    JSON.stringify(
      {
        schema: 1,
        engine: test.info().project.name,
        task: "new-feature-warm-work-editor",
        comparableBaselineTask: false,
        timing: "Editor controls visible; excludes resource-idle wait",
        renderMilliseconds,
        resources,
      },
      null,
      2,
    ),
  );
  await chooseChoice(page, "act.values.responsibleActor.kind", "SET");
  const roster = page.locator(".assignee-input");
  await roster.locator(`input[type="radio"][value="${actorId}"]`).check();
  await page.locator('input[data-selects-fields~="handlingSummary"]').check();
  await fillBusinessField(
    page,
    "handlingSummary",
    "Waiting for the requested documents.\nContact the claimant next week.",
  );
  await reviewDraft(page);
  await recordWorkReview(page, "WORK");
  await page.getByRole("button", { name: "Add reminder", exact: true }).click();
  await fillBusinessField(page, "reminderAction", "Request the remaining documents");
  await fillBusinessField(page, "reminderDueDate", planned);
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  const accepted = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (accepted.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected current case");
  }
  expect(accepted.outcome.data.revision).toBe(1);
  expect(accepted.outcome.data.work.revision).toBe(1);
  expect(accepted.outcome.data.work.responsible?.actorId).toBe(actorId);
  expect(accepted.outcome.data.work.followUpDate).toBe(planned);
  await expect(
    page
      .locator(".reminder-list .reminder-row")
      .filter({ hasText: "Request the remaining documents" })
      .locator(".reminder-timing"),
  ).toHaveText("Upcoming");
  await expectAccessible(page);
  await goToView(page, "cases");
  await page.getByRole("tab", { name: /^Mine(?:\s|$)/u }).click();
  await expect(page).toHaveURL((url) => url.searchParams.get("view") === "MINE");
  await expect(page.getByRole("link", { name: reference, exact: true })).toBeVisible();
});

for (const [entered, expected] of [
  ["en-GB", "1234"],
  ["lv-LV", "1.234"],
] as const) {
  test(`preserves the Settings detour and ${entered} amount meaning in a partial editor`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const reference = `ENTRY-${randomUUID()}`;
    await openCase(page, reference);
    await selectFormat(page, entered);
    await startAct(page, "Update claim details");
    await page.locator('input[data-selects-fields~="claimedAmount"]').check();
    await fillBusinessField(page, "claimedAmount", "1,234");
    const editorUrl = page.url();
    await (await settingsNavigation(page)).click();
    await expect(page).toHaveURL((url) => url.pathname === "/settings");
    await expect(page.locator(".settings-panel")).toBeVisible();
    await expect(
      page.getByRole("dialog", { name: "Discard these draft changes?", exact: true }),
    ).toHaveCount(0);
    await page.locator("[data-return-workspace]").click();
    await expect(page).toHaveURL(editorUrl);
    await expect(page.locator(".settings-panel")).toHaveCount(0);
    await expect(
      page.getByRole("dialog").locator('input[data-field-name="claimedAmount"]'),
    ).toHaveValue("1,234");
    const preview = await pauseJsonReply(page, "operation.preview");
    await page.getByRole("button", { name: "Review changes", exact: true }).click();
    const captured = await preview.ready;
    expect(JSON.parse(captured.bytes.toString("utf8"))).toMatchObject({
      act: {
        kind: "AMEND_REGISTRATION",
        values: { claimed: { amount: expected, currency: "EUR" } },
      },
    });
    expect(captured.reply.outcome.kind).toBe("REVIEWED");
    preview.release();
    await recordReviewed(page);
    const accepted = await currentCase(page, reference);
    expect(accepted.fields.registration.claimedAmount).toBe(expected);
    expect(accepted.fields.registration.insurerName).toBe("Synthetic insurer");
  });
}
