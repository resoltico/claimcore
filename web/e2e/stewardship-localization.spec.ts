import { showExactLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { openCase } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  noHorizontalOverflow,
  expectKeyboardContained,
  ui,
} from "./localization-support";

test("stewardship review preserves exact reason through pseudolocale, Arabic RTL and narrow keyboard interaction", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `STEWARD-LOCALE-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", "narrow-authenticated-state.json");
    const lookup = await showExactLookup(steward);
    await lookup.fill(reference);
    await lookup.press("Enter");
    await steward.locator("details.case-actions > summary").click();
    await steward.getByRole("button", { name: /Vööïïd/u }).click();
    const reason = "Exact 1.0000 العربية";
    await steward.locator('[name="act.reason"]').fill(reason);
    const preview = steward.getByRole("dialog").locator("button[type=submit]");
    const previewed = steward.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/operation.preview",
    );
    await preview.click();
    const response = await previewed;
    const reviewed: unknown = await response.json();
    if (!isResponse("operation.preview", reviewed) || reviewed.outcome.kind !== "REVIEWED") {
      throw new Error("Expected the real reviewed stewardship draft.");
    }
    const authored: unknown = JSON.parse(response.request().postData() ?? "null");
    expect(authored).toMatchObject({ act: { kind: "VOID", reason } });
    if (
      typeof authored !== "object" ||
      authored === null ||
      !("operationId" in authored) ||
      typeof authored.operationId !== "string"
    ) {
      throw new Error("Missing frozen stewardship identity.");
    }
    const { operationId } = authored;
    const dialog = steward.getByRole("dialog");
    await expect(dialog).toContainText(reason);
    await expect(dialog.getByRole("heading", { name: /Vööïïd/u })).toBeVisible();
    await steward.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(steward, "narrow");
    await expectKeyboardContained(steward, dialog);
    const requests: string[] = [];
    steward.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/operation.execute") {
        requests.push(request.postData() ?? "");
      }
    });
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAccessibleName(ui(language, "act.VOID.label"));
      await expect(dialog.locator("button[data-confirm-operation]")).toBeEnabled();
      await expect(dialog.locator(".review-request")).toContainText(operationId);
      await expect(dialog).toContainText(reason);
      await noHorizontalOverflow(steward, "narrow");
    }
    const lifecycle = dialog
      .locator(".review-values > div")
      .filter({ has: steward.getByText("Lifecycle", { exact: true }) });
    await expect(
      lifecycle.locator(".review-before").getByText("Active", { exact: true }),
    ).toBeVisible();
    await expect(
      lifecycle.locator(".review-after").getByText("Voided", { exact: true }),
    ).toBeVisible();
    expect(requests).toEqual([]);
    await expectAccessible(steward);
    await steward.keyboard.press("Escape");
    const guard = steward.getByRole("dialog");
    await expect(guard).toContainText("Discard this draft and leave the editor?");
    await guard.getByRole("button", { name: "Keep editing", exact: true }).click();
    await expect(dialog).toContainText(reason);
    await expect(dialog.locator(".review-request")).toContainText(operationId);
    await dialog.getByRole("button", { name: "Return to editing", exact: true }).click();
    await expect(preview).toBeFocused();
    await expect(steward.locator('[name="act.reason"]')).toHaveValue(reason);
    expect(requests).toEqual([]);
  } finally {
    await context.close();
  }
});
