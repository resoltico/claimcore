import { type ResponseFor } from "@contracts/client";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { goToView, chooseChoice } from "./workspace-navigation";
import { expect, test, type Page, type Locator } from "@playwright/test";
import { openAuthenticated, browserRequest, sessionToken } from "./session-helpers";
import { syntheticPrincipals } from "./people-setup";
import { selectLanguage, ui, trackForegroundRequests } from "./localization-support";
import { createPresenter } from "../src/presentation/presenter";
import { actorName } from "../src/presentation/actorName";
import { inputMessage } from "./input-accessibility-messages";
const previewRefusal = async (page: Page, control: Locator, target: string, code: string) => {
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await control.press("Enter");
  const reply = await pending;
  expect(reply.status()).toBe(200);
  const operationPreviewReply = (await reply.json()) as ResponseFor<"operation.preview", "HUMAN">;
  if (
    operationPreviewReply.representation !== "HUMAN" ||
    operationPreviewReply.outcome.kind !== "REJECTED"
  ) {
    throw new Error("Expected authoritative input refusal.");
  }
  if (operationPreviewReply.outcome.rejection.kind !== "INPUT") {
    throw new Error("Expected semantic input issues.");
  }
  expect(operationPreviewReply.outcome.rejection.advice.action).toBe("CORRECT_INPUT");
  expect(
    operationPreviewReply.outcome.rejection.issues.map((issue) => ({
      input: issue.input,
      row: issue.row,
      messageId: issue.message.messageId,
    })),
  ).toEqual([
    {
      input: target,
      row: null,
      messageId: `diagnostic.${code}`,
    },
  ]);
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog.locator("[data-error-summary]")).toBeFocused();
  await dialog.locator("[data-error-summary] a").click();
  await expect(control).toBeFocused();
  await expect(control).toHaveAttribute("aria-invalid", "true");
  await expect(dialog.locator("button[data-confirm-operation]")).toHaveCount(0);
};
const openPeople = async (page: Page, language: "en" | "lv" | "ar") => {
  await openAuthenticated(page);
  await goToView(page, "people");
  await selectLanguage(page, language);
};
const openAdmission = async (page: Page, language: "en" | "lv" | "ar") => {
  await page
    .getByRole("button", {
      name: ui(language, "task.people.admit.label"),
      exact: true,
    })
    .first()
    .click();
  const dialog = page.getByRole("dialog", {
    name: ui(language, "task.people.admit.label"),
    exact: true,
  });
  await dialog
    .locator('[name="input:profile.displayName"]')
    .fill("Authored accessibility candidate");
  await dialog
    .getByRole("radio", {
      name: ui(language, "role.CASE_READER.label"),
      exact: true,
    })
    .check();
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
      operationId: mintOperationId(),
      task: "people.admit",
      binding: { kind: "PEOPLE" },
      inputs: {
        "input:principal.kind": "HUMAN",
        "input:principal.issuer": issuer,
        "input:principal.subject": subject,
        "input:profile.displayName": "Authored accessibility candidate",
        "input:grants": [{ "input:role": "CASE_READER", "input:scope": "INSTALLATION" }],
      },
    }),
  });
  expect(result.status).toBe(200);
  const operationPreviewReply = result.payload as ResponseFor<"operation.preview", "DATA">;
  if (
    operationPreviewReply.representation !== "DATA" ||
    operationPreviewReply.outcome.kind !== "REJECTED"
  ) {
    throw new Error("Expected actual malformed principal refusal.");
  }
  expect(operationPreviewReply.outcome.rejection).toEqual({
    code: "INVALID_INPUT",
    action: "CORRECT_INPUT",
    issues: [
      {
        input: "input:principal.issuer",
        code: "PRINCIPAL_FORMAT",
        row: null,
      },
    ],
  });
};
for (const language of ["en", "lv", "ar"] as const) {
  test(`seeded issuer and subject refusals preserve exact accessible names and keyboard focus in ${language}`, async ({
    page,
  }) => {
    await openPeople(page, language);
    const requests = trackForegroundRequests(page);
    const source = await syntheticPrincipals();
    const dialog = await openAdmission(page, language);
    const issuer = dialog.locator('[name="input:principal.issuer"]');
    const subject = dialog.locator('[name="input:principal.subject"]');
    await expect(issuer).toHaveValue(source.issuer);
    await expect(issuer).toBeEnabled();
    await expect(issuer).toHaveAccessibleName(
      inputMessage(language, "field.principal.issuer.label"),
    );
    await expect(issuer).toHaveAccessibleDescription(
      inputMessage(language, "field.principal.issuer.hint"),
    );
    // The default issuer is a seed; the direct DATA probe independently exercises malformed principal admission.
    await malformedIssuerBoundary(page, "http://invalid.example/", source.ownerSubject);
    await expect(issuer).not.toHaveAttribute("aria-invalid");
    await subject.focus();
    await expect(subject).toBeFocused();
    await subject.fill("");
    await previewRefusal(page, subject, "input:principal.subject", "TEXT_REQUIRED");
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
    await openPeople(page, language);
    const requests = trackForegroundRequests(page);
    const own = await browserRequest(page, "/api/context", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: "{}",
    });
    const contextReply = own.payload as ResponseFor<"context", "DATA">;
    if (
      contextReply.representation !== "DATA" ||
      contextReply.outcome.kind !== "SUCCEEDED" ||
      contextReply.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("Expected the current owner context.");
    }
    const { requester } = contextReply.outcome.data;
    const { actorId } = requester;
    const p = createPresenter({
      language,
      displayLocale: "en-GB",
      mode: "system",
      accent: "green",
    });
    const person = page.getByRole("dialog", {
      name: actorName(p, requester.self),
      exact: true,
    });
    await page.goto(`/people/${encodeURIComponent(actorId)}`, {
      waitUntil: "commit",
    });
    for (const kind of ["people.grant", "people.revoke"] as const) {
      await expect(person).toBeVisible();
      const name = p.text("ui.actDescription", {
        label: p.peopleLabel(kind),
        meaning: p.peopleMeaning(kind),
      });
      const action = person.getByRole("button", {
        name,
        exact: true,
      });
      await expect(action).toHaveText(ui(language, `task.${kind}.label`));
      await action.click();
      const dialog = page.getByRole("dialog", {
        name: ui(language, `task.${kind}.label`),
        exact: true,
      });
      await expect(dialog.locator('[name="input:principal.issuer"]')).toHaveCount(0);
      await expect(dialog.locator('[name="input:principal.subject"]')).toHaveCount(0);
      await chooseChoice(page, "input:role", "CASE_READER");
      await chooseChoice(page, "input:scope", "CASE");
      const reference = dialog.locator('[name="field:caseReference"]');
      await reference.fill("");
      await previewRefusal(page, reference, "field:caseReference", "TEXT_REQUIRED");
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
      if (kind === "people.grant") {
        await dialog
          .getByRole("button", {
            name: ui(language, "ui.close"),
            exact: true,
          })
          .first()
          .click();
        const guard = page.getByRole("dialog", {
          name: ui(language, "ui.changeActTitle"),
          exact: true,
        });
        await guard
          .getByRole("button", {
            name: ui(language, "ui.discardAndLeave"),
            exact: true,
          })
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
