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
  if (!isResponse("operation.preview", value) || value.outcome.tag !== "REJECTED") {
    throw new Error("Expected authoritative input refusal.");
  }
  expect(value.outcome.data).toMatchObject({ target, code, action: "CORRECT_INPUT" });
  await expect(control).toBeFocused();
  await expect(control).toHaveAttribute("aria-invalid", "true");
  await expect(page.getByRole("dialog")).toHaveCount(0);
};
const openAuthority = async (page: Page, language: "en" | "lv" | "ar") => {
  await openAuthenticated(page);
  await page.getByRole("button", { name: "Authority", exact: true }).click();
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
    await previewRefusal(page, issuer, "principal.issuer", "INPUT_PRINCIPAL_FORMAT");
    await expect(issuer).toHaveAccessibleName(inputMessage(language, "input.issuer.label"));
    await expect(issuer).toHaveAccessibleDescription(
      inputMessage(language, "diagnostic.INPUT_PRINCIPAL_FORMAT"),
    );
    await issuer.fill(source.issuer);
    await expect(issuer).not.toHaveAttribute("aria-invalid");
    await expect(issuer).toHaveAccessibleDescription("");
    await issuer.press("Tab");
    await expect(subject).toBeFocused();
    await subject.fill("");
    await previewRefusal(page, subject, "principal.subject", "INPUT_TEXT_REQUIRED");
    await expect(subject).toHaveAccessibleName(inputMessage(language, "input.subject.label"));
    await expect(subject).toHaveAccessibleDescription(
      inputMessage(language, "diagnostic.INPUT_TEXT_REQUIRED"),
    );
    await subject.fill(source.ownerSubject);
    await expect(subject).not.toHaveAttribute("aria-invalid");
    await expect(subject).toHaveAccessibleDescription("");
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
      await page.locator('[name="target.issuer"]').fill(source.issuer);
      await page.locator('[name="target.subject"]').fill(source.ownerSubject);
      await page.locator('[name="act.role"]').selectOption("CASE_READER");
      await page.locator('[name="act.scope.kind"]').selectOption("CASE");
      await reference.fill("");
      await previewRefusal(page, reference, "caseReference", "INPUT_TEXT_REQUIRED");
      await expect(reference).toHaveAccessibleName(
        inputMessage(language, "field.caseReference.label"),
      );
      await expect(reference).toHaveAccessibleDescription(
        new RegExp(
          inputMessage(language, "diagnostic.INPUT_TEXT_REQUIRED").replace(
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
