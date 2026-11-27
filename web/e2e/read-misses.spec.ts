import { pseudolocalizeLiteral } from "../src/presentation/templates";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { openRecovery, showExactLookup, showOperationLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";

import { expect, test, type Page } from "./fixtures";

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
import { openCase } from "./case-workflow";

test("associates malformed operation lookup guidance and prevents empty case lookup", async ({
  page,
}) => {
  await openAuthenticated(page);
  await expect(page.getByRole("button", { name: "Refresh", exact: true })).toBeEnabled();
  const lookup = await showExactLookup(page);
  const requests = trackForegroundRequests(page);
  await lookup.fill("");
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
  await (await showExactLookup(page)).fill(`ABSENT-${randomUUID().slice(0, 8)}`);
  await (await showExactLookup(page)).press("Enter");
  const find = page.getByRole("dialog", { name: "Find a case" });
  await expect(find.getByRole("alert")).toBeVisible();
  await expect(page.locator("#case-detail-title")).toHaveCount(0);
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

const caseReadAt = async (page: Page, path: string) => {
  const held = await pauseJsonReply(page, "case.get");
  try {
    await page.goto(path);
    const { reply } = await held.ready;
    expect(reply.outcome.kind).toBe("REJECTED");
    if (reply.outcome.kind !== "REJECTED") {
      throw new Error("Expected the case selector to be refused.");
    }
    return reply.outcome.rejection;
  } finally {
    held.release();
  }
};

test("recovers from a malformed reference through the existing Find dialog", async ({ page }) => {
  await openAuthenticated(page);
  const { caseReference: reference } = await openCase(
    page,
    `FIND-ROUTE-${randomUUID().slice(0, 8).toUpperCase()}`,
  );
  const casePath = new URL(page.url()).pathname;
  const malformed = "CC-0000-0000-0000-0000-01";
  const malformedPath = `/cases/reference/${malformed}`;
  const main = page.getByRole("main");
  await page.setViewportSize({ width: 320, height: 720 });

  const english = await caseReadAt(page, malformedPath);
  expect(english).toMatchObject({
    kind: "INPUT",
    advice: { action: "CORRECT_INPUT" },
    issues: [
      {
        input: "field:caseReference",
        row: null,
        message: { messageId: "diagnostic.REFERENCE_FORMAT", arguments: [] },
      },
    ],
  });
  await expect(main.getByRole("alert")).toHaveText(ui("en", "diagnostic.REFERENCE_FORMAT.gui"));
  await expect(main.getByRole("heading", { level: 1 })).toHaveText(malformed);
  await expect(main.getByText("Fields to check: 1.", { exact: true })).toHaveCount(0);
  await expect(main.getByRole("button", { name: "Try again", exact: true })).toHaveCount(0);
  await expect(page.locator(".operation-form")).toHaveCount(0);

  await selectLanguage(page, "lv");
  expect(await caseReadAt(page, malformedPath)).toEqual(english);
  await expect(main.getByRole("alert")).toHaveText(ui("lv", "diagnostic.REFERENCE_FORMAT.gui"));
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);

  const findName = ui("lv", "ui.findCase");
  const launcher = main.getByRole("button", { name: findName, exact: true });
  const dialog = page.getByRole("dialog", { name: findName });
  const search = dialog.getByRole("combobox");
  await launcher.press("Enter");
  await expect(page.getByRole("dialog")).toHaveCount(1);
  await expect(search).toBeFocused();
  await expect(search).toHaveValue("");
  await search.press("Escape");
  await expect(launcher).toBeFocused();
  await launcher.press("Enter");
  await search.fill(reference.toLowerCase());
  await search.press("Enter");
  await expect(page).toHaveURL((url) => url.pathname === casePath);
  await expect
    .poll(() =>
      page.evaluate(() => ({
        id: document.activeElement?.id,
        tag: document.activeElement?.tagName,
      })),
    )
    .toEqual({ id: "case-detail-title", tag: "H1" });

  await page.goBack();
  await expect(page).toHaveURL((url) => url.pathname === malformedPath);
  await expect(launcher).toBeVisible();
  await expect
    .poll(() => main.evaluate((area) => area.contains(document.activeElement)))
    .toBe(true);

  const absent = await caseReadAt(page, "/cases/reference/CC-7K3M-Q8T2-PW6D-X9HR-49");
  expect(absent).toMatchObject({
    kind: "REFUSED",
    advice: { action: "ASK_FOR_ACCESS" },
  });
  await expect(main.getByRole("alert")).toBeVisible();
  await expect(main.getByRole("alert")).not.toHaveText(ui("lv", "diagnostic.REFERENCE_FORMAT.gui"));
  await expect(launcher).toHaveCount(0);
  await expect(page.locator(".operation-form")).toHaveCount(0);
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
    await expect(submit).toBeEnabled();
    await submit.focus();
    await expect(submit).toBeFocused();
    await page.keyboard.press("Enter");
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
