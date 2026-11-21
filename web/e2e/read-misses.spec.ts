import { pseudolocalizeLiteral } from "../src/presentation/templates";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { openRecovery, showExactLookup, showOperationLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";

import { expect, test } from "./fixtures";

import { expectAccessible, openAuthenticated } from "./session-helpers";
import {
  selectLanguage,
  trackForegroundRequests,
  ui,
  seedPresentation,
  pauseJsonReply,
  noHorizontalOverflow,
} from "./localization-support";
import { endpoints } from "@contracts/client";

test("associates malformed operation lookup guidance and prevents empty case lookup", async ({
  page,
}) => {
  await openAuthenticated(page);
  await expect(page.getByRole("button", { name: "Reload cases", exact: true })).toBeEnabled();
  const lookup = await showExactLookup(page);
  const requests = trackForegroundRequests(page);
  await lookup.fill("");
  await expect(
    page.getByRole("button", { name: "Open by exact reference", exact: true }),
  ).toBeDisabled();
  await lookup.press("Enter");
  expect(requests).toHaveLength(0);
  await openRecovery(page);
  const operation = await showOperationLookup(page);
  await page.waitForLoadState("networkidle");
  await expect(page.locator('.workspace-sidebar a[href="/cases"]')).toBeVisible();
  // Wait for the verified workspace before checking its authenticated reads.
  expect([...requests].sort()).toEqual(["POST /api/context", "POST /api/work.views"].sort());
  requests.length = 0;
  await expect(operation).toHaveAccessibleName("Exact operation ID");
  await operation.fill("not-an-operation-id");
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(operation).toBeFocused();
  await expect(operation).toHaveAttribute("aria-invalid", "true");
  await expect(operation).toHaveAttribute("aria-describedby", /\S/u);
  const before = await page.getByRole("alert").textContent();
  expect(before).toBeTruthy();
  await expect(operation).toHaveAccessibleDescription(before ?? "");
  await selectLanguage(page, "lv");
  await expect(operation).toHaveAccessibleName(ui("lv", "ui.exactOperationId"));
  await expect(page.getByRole("alert")).not.toHaveText(before ?? "");
  await expect(operation).toHaveAttribute("aria-invalid", "true");
  const translated = await page.getByRole("alert").textContent();
  expect(translated).toBeTruthy();
  await expect(operation).toHaveAccessibleDescription(translated ?? "");
  await operation.fill(mintOperationId());
  await expect(operation).not.toHaveAttribute("aria-invalid");
  const observe = endpoints.find((endpoint) => endpoint.id === "operation.get");
  expect(observe).toBeDefined();
  expect(requests).toEqual([`POST ${observe?.path}`]);
  await expectAccessible(page);
});

test("keeps absent cases private and unknown operation outcomes pending", async ({ page }) => {
  await openAuthenticated(page);
  await (await showExactLookup(page)).fill(`ABSENT-${randomUUID()}`);
  await (await showExactLookup(page)).press("Enter");
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Case was not found.", { exact: true })).toHaveCount(0);
  await expect(page.getByText(ui("en", "ui.loadingHistory"), { exact: true })).toHaveCount(0);
  await expectAccessible(page);
  await openRecovery(page);
  await (await showOperationLookup(page)).fill(mintOperationId());
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Operation was not observed.", { exact: true })).toHaveCount(0);
  await expectAccessible(page);
});

for (const language of ["ar", "en-XA"] as const) {
  test(`published pending lookup preserves its supplied deadline and reflows accessibly in ${language} at 320px`, async ({
    page,
  }) => {
    await seedPresentation(page, language, "ar-EG");
    await page.setViewportSize({ width: 320, height: 720 });
    await openAuthenticated(
      page,
      language === "ar"
        ? ui("ar", "label.cases")
        : `⟦${pseudolocalizeLiteral(ui("en", "label.cases"))}⟧`,
    );
    await openRecovery(page);
    const operationId = mintOperationId();
    const field = await showOperationLookup(page);
    const scope = page.locator('section[aria-labelledby="operation-lookup-title"]');
    const submit = scope.locator('button[type="submit"]');
    const first = await pauseJsonReply(page, "operation.get");
    await field.fill(operationId);
    await submit.press("Enter");
    const captured = await first.ready;
    first.release();
    expect(captured.reply.operationId).toBe(operationId);
    expect(captured.reply.outcome.kind).toBe("PENDING");
    if (captured.reply.outcome.kind !== "PENDING") {
      throw new Error("E2E_EXPECTED_UNKNOWN_OPERATION_PENDING");
    }
    const { finalAt, document } = captured.reply.outcome;
    const supplied = document.screen.regions.find((region) => region.content.kind === "NOTICE");
    expect(supplied?.content).toEqual({
      kind: "NOTICE",
      notices: [
        {
          messageId: "label.operationPending",
          tone: "INFO",
          reason: { kind: "INSTANT", value: finalAt },
        },
      ],
    });
    const meaning =
      language === "ar"
        ? ui("ar", "label.operationPending")
        : `⟦${pseudolocalizeLiteral(ui("en", "label.operationPending"))}⟧`;
    const notice = scope.locator('.region-notice[data-tone="INFO"]');
    await expect(notice).toHaveAttribute("role", "status");
    await expect(notice).toContainText(meaning);
    const deadline = notice.locator("time");
    await expect(deadline).toHaveAttribute("datetime", finalAt);
    await expect(deadline).toHaveAttribute("title", finalAt);
    await expect(deadline).not.toHaveText(finalAt);
    await expect(deadline).toContainText(/[٠-٩]/u);
    await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
    await expect(field).toHaveValue(operationId);
    await noHorizontalOverflow(page, "narrow");
    const bounds = await notice.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(-1);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(321);
    await expectAccessible(page);
    // Keyboard re-observation retains the original identity and canonical deadline.
    const second = await pauseJsonReply(page, "operation.get");
    await expect(submit).toBeEnabled();
    await submit.focus();
    await expect(submit).toBeFocused();
    await page.keyboard.press("Enter");
    const observed = await second.ready;
    second.release();
    expect(observed.bytes.equals(captured.bytes)).toBe(true);
    expect(observed.reply.operationId).toBe(operationId);
    expect(observed.reply.outcome).toMatchObject({ kind: "PENDING", finalAt });
    await expect(deadline).toHaveAttribute("datetime", finalAt);
    await noHorizontalOverflow(page, "narrow");
  });
}
