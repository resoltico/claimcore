import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, startOpen, startCommand } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";
import { confirmPrepared, pauseJsonReply, selectLanguage, ui } from "./localization-support";

for (const language of ["en", "lv", "ar"] as const) {
  test(`refuses localized authoring digits and currency alphabets in ${language} [CC-DOM-001]`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await startOpen(page, `SYNTAX-${randomUUID()}`);
    await selectLanguage(page, language);
    for (const [name, value, diagnostic] of [
      ["claimedAmount", "١٢٫٥٠", "INPUT_DECIMAL_FORMAT"],
      ["claimedAmount", "125,50", "INPUT_DECIMAL_FORMAT"],
      ["incidentDate", "٢٠٢٦-٠٩-٠١", "INPUT_CALENDAR_DATE_REQUIRED"],
      ["claimedCurrency", "ĀBC", "INPUT_CURRENCY_FORMAT"],
    ]) {
      await page.locator(`input[name="${name}"]`).fill(value!);
      const pending = await pauseJsonReply(page, "command.prepare");
      await page
        .getByRole("button", { name: ui(language, "ui.prepareExact"), exact: true })
        .click();
      const captured = await pending.ready;
      expect(captured.reply.outcome.tag).toBe("REJECTED");
      if (captured.reply.outcome.tag === "REJECTED") {
        expect(captured.reply.outcome.data.rejection.diagnostic.id).toBe(diagnostic);
      }
      pending.release();
      await expect(page.getByRole("alert")).toBeVisible();
      await page.locator('input[name="claimedAmount"]').fill("1200.50");
      await page.locator('input[name="incidentDate"]').fill("2026-09-01");
      await page.locator('input[name="claimedCurrency"]').fill("EUR");
    }
  });
  test(`refuses corrections of missing recorded values in ${language} [CC-DOM-002]`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await openCase(page, `MISSING-${randomUUID()}`);
    await startCommand(page, "Close the case");
    // Close through the existing native workflow, then try to correct an absent decision.
    const first = await pauseJsonReply(page, "command.prepare");
    await page.getByRole("button", { name: "Review changes" }).click();
    await first.ready;
    first.release();
    await confirmPrepared(page);
    await page.getByRole("button", { name: "Record changes" }).click();
    await page.getByRole("button", { name: "Return to case" }).click();
    await startCommand(page, "Correct case facts");
    await selectLanguage(page, language);
    await page.locator("#correction-decision-mode").selectOption("CLEAR");
    const pending = await pauseJsonReply(page, "command.prepare");
    await page.getByRole("button", { name: ui(language, "ui.prepareExact"), exact: true }).click();
    const captured = await pending.ready;
    expect(captured.reply.outcome.tag).toBe("REJECTED");
    if (captured.reply.outcome.tag === "REJECTED") {
      expect(captured.reply.outcome.data.rejection.diagnostic.id).toBe(
        "CORRECTION_EXISTING_VALUE_REQUIRED",
      );
    }
    pending.release();
    await expect(page.getByRole("alert")).toBeVisible();
  });
}
