import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { startOpen, reviewDraft } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";
import { ui } from "./localization-support";
import { isRequest, isResponse } from "../src/protocol/client";

for (const [authored, ascii] of [
  ["ß", "SS"],
  ["ſ", "S"],
] as const) {
  test(`preview correlation does not fold ${authored} into ${ascii}`, async ({ page }) => {
    await openAuthenticated(page);
    const suffix = randomUUID();
    await startOpen(page, `${ascii.toLowerCase()}-${suffix}`);
    const pending = page.waitForResponse((response) =>
      response.url().endsWith("/api/operation.preview"),
    );
    await reviewDraft(page);
    const template: unknown = await (await pending).json();
    if (!isResponse("operation.preview", template) || template.outcome.kind !== "REVIEWED") {
      throw new Error("The real ASCII preview positive control must admit");
    }
    const { data } = template.outcome;
    const { subject } = data.review;
    if (subject.kind !== "CASE") {
      throw new Error("Expected case preview");
    }
    await page
      .getByRole("dialog")
      .getByRole("button", { name: ui("en", "ui.returnToEdit"), exact: true })
      .click();
    await page
      .getByLabel("Handler's case reference", { exact: true })
      .fill(`${authored}-${suffix}`);
    let forgedReplies = 0;
    await page.route("**/api/operation.preview", async (route) => {
      const input: unknown = route.request().postDataJSON();
      if (
        !isRequest("operation.preview", input) ||
        input.kind !== "CASE" ||
        input.act.kind !== "OPEN"
      ) {
        throw new Error("Expected a structurally admitted OPEN request");
      }
      const forged = {
        operationId: input.operationId,
        outcome: {
          kind: "REVIEWED",
          data: {
            ...data,
            review: {
              ...data.review,
              subject: { ...subject, after: { ...subject.after, caseId: input.operationId } },
            },
          },
        },
      };
      expect(isResponse("operation.preview", forged)).toBe(true);
      forgedReplies += 1;
      await route.fulfill({ status: 200, json: forged });
    });
    await page.getByRole("button", { name: "Review changes", exact: true }).click();
    await expect(page.getByRole("alert")).toContainText(
      ui("en", "notice.invalidHttp").replace("{status}", "200"),
    );
    expect(forgedReplies).toBe(1);
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });
}
