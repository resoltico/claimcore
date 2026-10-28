import { expect, test, type Page, type Locator } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { syntheticPrincipals } from "./authority-setup";
import { selectLanguage, ui } from "./localization-support";
import { inputMessage } from "./input-accessibility-messages";
import { isResponse } from "../src/protocol/client";

const previewRefusal = async (page: Page, control: Locator, target: string, code: string) => {
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await control.press("Enter");
  const reply = await pending;
  expect(reply.status()).toBe(200);
  const value: unknown = await reply.json();
  if (!isResponse("operation.preview", value) || value.outcome.kind !== "REJECTED") {
    throw new Error("Expected authoritative input refusal.");
  }
  expect(value.outcome.data).toEqual({
    code: "INVALID_INPUT",
    action: "CORRECT_INPUT",
    issues: [{ field: target, code }],
  });
  await expect(page.locator("[data-error-summary]")).toBeFocused();
  await page.locator("[data-error-summary] a").click();
  await expect(control).toBeFocused();
  await expect(control).toHaveAttribute("aria-invalid", "true");
  await expect(page.getByRole("dialog")).toHaveCount(0);
};
const openAuthority = async (page: Page, language: "en" | "lv" | "ar") => {
  await openAuthenticated(page);
  await page.getByRole("link", { name: "Authority", exact: true }).click();
  await selectLanguage(page, language);
};
for (const language of ["en", "lv", "ar"] as const) {
  test(`principal issuer and subject refusals preserve exact accessible names and keyboard focus in ${language}`, async ({
    page,
  }) => {
    await openAuthority(page, language);
    const source = await syntheticPrincipals();
    const issuer = page.locator('[name="target.issuer"]');
    const subject = page.locator('[name="target.subject"]');
    await issuer.fill("http://invalid.example/");
    await subject.fill(source.ownerSubject);
    await previewRefusal(page, issuer, "principal.issuer", "PRINCIPAL_FORMAT");
    await expect(issuer).toHaveAccessibleName(
      inputMessage(language, "field.principal.issuer.label"),
    );
    await expect(issuer).toHaveAccessibleDescription(
      `${inputMessage(language, "field.principal.issuer.meaning")} ${inputMessage(language, "diagnostic.PRINCIPAL_FORMAT")}`,
    );
    await issuer.fill(source.issuer);
    await expect(issuer).not.toHaveAttribute("aria-invalid");
    await expect(issuer).toHaveAccessibleDescription(
      inputMessage(language, "field.principal.issuer.meaning"),
    );
    await issuer.press("Tab");
    await expect(subject).toBeFocused();
    await subject.fill("");
    await previewRefusal(page, subject, "principal.subject", "TEXT_REQUIRED");
    await expect(subject).toHaveAccessibleName(
      inputMessage(language, "field.principal.subject.label"),
    );
    await expect(subject).toHaveAccessibleDescription(
      `${inputMessage(language, "field.principal.subject.meaning")} ${inputMessage(language, "diagnostic.TEXT_REQUIRED")}`,
    );
    await subject.fill(source.ownerSubject);
    await expect(subject).not.toHaveAttribute("aria-invalid");
    await expect(subject).toHaveAccessibleDescription(
      inputMessage(language, "field.principal.subject.meaning"),
    );
  });
  test(`case-scoped grant and revoke references retain shared hints and refusal focus in ${language}`, async ({
    page,
  }) => {
    await openAuthority(page, language);
    const source = await syntheticPrincipals();
    const reference = page.locator('[name="act.scope.caseReference"]');
    for (const kind of ["GRANT", "REVOKE"]) {
      await page
        .getByRole("combobox", { name: ui(language, "ui.authorityAction"), exact: true })
        .selectOption(kind);
      if (kind === "REVOKE") {
        const dialog = page.getByRole("dialog", { name: ui(language, "ui.changeActTitle") });
        await dialog
          .getByRole("button", { name: ui(language, "ui.discardAndChange"), exact: true })
          .click();
        await expect(dialog).toHaveCount(0);
      }
      await page.locator('[name="target.issuer"]').fill(source.issuer);
      await page.locator('[name="target.subject"]').fill(source.ownerSubject);
      await page.locator('[name="act.role"]').selectOption("CASE_READER");
      await page.locator('[name="act.scope.kind"]').selectOption("CASE");
      await reference.fill("");
      await previewRefusal(page, reference, "caseReference", "TEXT_REQUIRED");
      await expect(reference).toHaveAccessibleName(
        inputMessage(language, "field.caseReference.label"),
      );
      await expect(reference).toHaveAccessibleDescription(
        new RegExp(
          inputMessage(language, "diagnostic.TEXT_REQUIRED").replace(
            /[.*+?^${}()|[\]\\]/gu,
            "\\$&",
          ),
          "u",
        ),
      );
      await expect(page.locator(".field-description")).toContainText(
        inputMessage(language, "field.caseReference.meaning"),
      );
      await reference.fill("Synthetic reference");
      await expect(reference).not.toHaveAttribute("aria-invalid");
      await expect(reference).toHaveAccessibleName(
        inputMessage(language, "field.caseReference.label"),
      );
    }
  });
}
