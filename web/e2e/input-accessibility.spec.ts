import { operationId as mintOperationId } from "../src/operation/operationId";
import { goToView, chooseChoice } from "./workspace-navigation";
import { expect, test, type Page, type Locator } from "@playwright/test";
import { openAuthenticated, browserRequest, sessionToken } from "./session-helpers";
import { syntheticPrincipals } from "./authority-setup";
import {
  selectLanguage,
  ui,
  trackForegroundRequests,
  installTestLanguage,
} from "./localization-support";
import { createPresenter } from "../src/presentation/presenter";
import { actorName } from "../src/presentation/actorName";
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
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog.locator("[data-error-summary]")).toBeFocused();
  await dialog.locator("[data-error-summary] a").click();
  await expect(control).toBeFocused();
  await expect(control).toHaveAttribute("aria-invalid", "true");
  await expect(dialog.locator("button[data-confirm-operation]")).toHaveCount(0);
};
const openAuthority = async (page: Page, language: "en" | "lv" | "ar") => {
  await openAuthenticated(page);
  await goToView(page, "people");
  await selectLanguage(page, language);
};
const openAdmission = async (page: Page, language: "en" | "lv" | "ar") => {
  await page
    .getByRole("button", { name: ui(language, "act.ADMIT.label"), exact: true })
    .first()
    .click();
  const dialog = page.getByRole("dialog", { name: ui(language, "act.ADMIT.label"), exact: true });
  await dialog.locator('[name="act.profile.displayName"]').fill("Authored accessibility candidate");
  await dialog.locator('input[name$=".role"][value="CASE_READER"]').check();
  return dialog;
};
const malformedIssuerBoundary = async (page: Page, issuer: string, subject: string) => {
  const result = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      kind: "AUTHORITY",
      operationId: mintOperationId(),
      target: { kind: "HUMAN", issuer, subject },
      act: {
        kind: "ADMIT",
        profile: { displayName: "Authored accessibility candidate", contact: null },
        grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
      },
    }),
  });
  expect(result.status).toBe(200);
  if (
    !isResponse("operation.preview", result.payload) ||
    result.payload.outcome.kind !== "REJECTED"
  ) {
    throw new Error("Expected actual malformed principal refusal.");
  }
  expect(result.payload.outcome.data).toEqual({
    code: "INVALID_INPUT",
    action: "CORRECT_INPUT",
    issues: [{ field: "principal.issuer", code: "PRINCIPAL_FORMAT" }],
  });
};
for (const language of ["en", "lv", "ar"] as const) {
  test(`fixed issuer and subject refusals preserve exact accessible names and keyboard focus in ${language}`, async ({
    page,
  }) => {
    await openAuthority(page, language);
    const requests = trackForegroundRequests(page);
    const source = await syntheticPrincipals();
    const dialog = await openAdmission(page, language);
    const issuer = dialog.locator('[name="target.issuer"]');
    const subject = dialog.locator('[name="target.subject"]');
    await expect(issuer).toHaveValue(source.issuer);
    await expect(issuer).toBeDisabled();
    await expect(issuer).toHaveAccessibleName(
      inputMessage(language, "field.principal.issuer.label"),
    );
    await expect(issuer).toHaveAccessibleDescription(
      inputMessage(language, "field.principal.issuer.hint"),
    );
    // Sole-provider issuer is bound in the real UI. Keep its malformed-authored boundary as a real API probe, without enabling a disabled field.
    await malformedIssuerBoundary(page, "http://invalid.example/", source.ownerSubject);
    await expect(issuer).not.toHaveAttribute("aria-invalid");
    await subject.focus();
    await expect(subject).toBeFocused();
    await subject.fill("");
    await previewRefusal(page, subject, "principal.subject", "TEXT_REQUIRED");
    await expect(subject).toHaveAccessibleName(
      inputMessage(language, "field.principal.subject.label"),
    );
    await expect(subject).toHaveAccessibleDescription(
      `${inputMessage(language, "field.principal.subject.hint")} ${inputMessage(language, "diagnostic.TEXT_REQUIRED")}`,
    );
    await subject.fill(source.ownerSubject);
    await expect(subject).not.toHaveAttribute("aria-invalid");
    await expect(subject).toHaveAccessibleDescription(
      inputMessage(language, "field.principal.subject.hint"),
    );
    await expect(dialog.locator("button[data-confirm-operation]")).toHaveCount(0);
    expect(requests.filter((request) => request === "POST /api/operation.execute")).toEqual([]);
  });
  test(`case-scoped grant and revoke references retain shared hints and refusal focus in ${language}`, async ({
    page,
  }) => {
    await openAuthority(page, language);
    const requests = trackForegroundRequests(page);
    const source = await syntheticPrincipals();
    const own = await browserRequest(page, "/api/context", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: "{}",
    });
    if (
      !isResponse("context", own.payload) ||
      own.payload.outcome.kind !== "SUCCEEDED" ||
      own.payload.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("Expected the current owner context.");
    }
    const { requester } = own.payload.outcome.data;
    const { actorId } = requester;
    installTestLanguage(language);
    const p = createPresenter({
      language,
      displayLocale: "en-GB",
      mode: "system",
      accent: "green",
    });
    const person = page.getByRole("dialog", { name: actorName(p, requester.self), exact: true });
    await page.goto(`/people/${encodeURIComponent(actorId)}`, { waitUntil: "commit" });
    for (const kind of ["GRANT", "REVOKE"] as const) {
      await expect(person).toBeVisible();
      const name = p.text("ui.actDescription", {
        label: p.authorityLabel(kind),
        meaning: p.authorityMeaning(kind),
      });
      const action = person.getByRole("button", { name, exact: true });
      await expect(action).toHaveText(ui(language, `act.${kind}.label`));
      await action.click();
      const dialog = page.getByRole("dialog", {
        name: ui(language, `act.${kind}.label`),
        exact: true,
      });
      await expect(dialog.locator('[name="target.issuer"]')).toHaveValue(source.issuer);
      await expect(dialog.locator('[name="target.subject"]')).toHaveValue(source.ownerSubject);
      await expect(dialog.locator('[name="target.subject"]')).toBeDisabled();
      await chooseChoice(page, "act.role", "CASE_READER");
      await chooseChoice(page, "act.scope.kind", "CASE");
      const reference = dialog.locator('[name="act.scope.target.caseReference"]');
      await reference.fill("");
      await previewRefusal(page, reference, "caseReference", "TEXT_REQUIRED");
      await expect(reference).toHaveAccessibleName(
        inputMessage(language, "field.caseReference.label"),
      );
      await expect(reference).toHaveAccessibleDescription(
        `${inputMessage(language, "field.caseReference.hint")} ${inputMessage(language, "diagnostic.TEXT_REQUIRED")}`,
      );
      await reference.fill("Synthetic reference");
      await expect(reference).not.toHaveAttribute("aria-invalid");
      await expect(reference).toHaveAccessibleName(
        inputMessage(language, "field.caseReference.label"),
      );
      if (kind === "GRANT") {
        await dialog
          .getByRole("button", { name: ui(language, "ui.close"), exact: true })
          .first()
          .click();
        const guard = page.getByRole("dialog", {
          name: ui(language, "ui.changeActTitle"),
          exact: true,
        });
        await guard
          .getByRole("button", { name: ui(language, "ui.discardAndLeave"), exact: true })
          .click();
        await expect(guard).toHaveCount(0);
        await expect(person).toBeVisible();
        await expect(page).toHaveURL(
          (url) => url.pathname === `/people/${encodeURIComponent(actorId)}`,
        );
      }
    }
    expect(requests.filter((request) => request === "POST /api/operation.execute")).toEqual([]);
  });
}
