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
import { pauseJsonReply, selectFormat } from "./localization-support";

test("records future planned work and scoped responsibility, then finds My cases", async ({
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
  if (!isResponse("context", context.payload) || context.payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("Work context unavailable");
  }
  const { actorId, businessDate } = context.payload.outcome.data;
  const tomorrow = new Date(`${businessDate}T12:00:00Z`);
  tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
  const planned = tomorrow.toISOString().slice(0, 10);
  const beforeResources = await page.evaluate(
    () => window.performance.getEntriesByType("resource").length,
  );
  const started = performance.now();
  await startAct(page, "Update case work");
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
  await chooseChoice(page, "act.values.followUpDate.kind", "SET");
  await fillBusinessField(page, "followUpDate", planned);
  await reviewDraft(page);
  await recordReviewed(page, "I confirm these work changes.");
  const accepted = await currentCase(page, reference);
  expect(accepted.fields.work).toEqual({ responsibleActor: actorId, followUpDate: planned });
  await expect(page.locator(".case-work")).toContainText("Upcoming");
  await expectAccessible(page);
  await goToView(page, "cases");
  await page.getByRole("button", { name: "My cases", exact: true }).click();
  await expect(page.getByRole("link", { name: reference, exact: true })).toBeVisible();
});

for (const [entered, display, expected] of [
  ["en-GB", "lv-LV", "1234"],
  ["lv-LV", "en-GB", "1.234"],
] as const) {
  test(`keeps ${entered} amount meaning after switching to ${display} in a connected partial editor`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const reference = `ENTRY-${randomUUID()}`;
    await openCase(page, reference);
    await selectFormat(page, entered);
    await startAct(page, "Amend registration");
    await page.locator('input[data-selects-fields~="claimedAmount"]').check();
    await fillBusinessField(page, "claimedAmount", "1,234");
    await selectFormat(page, display);
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
