import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { inputMessage } from "./input-accessibility-messages";
import {
  pauseJsonReply,
  confirmReview,
  selectLanguage,
  ui,
  trackRequests,
} from "./localization-support";
import {
  chooseGroups,
  currentCase,
  history,
  paidCase,
  expectResultFields,
  type Modes,
} from "./localization-correction-workflow";

// Each independent case is accepted through the authenticated service before browser-authored
// correction, language changes and authoritative readback.
test.setTimeout(45_000);

// The mode cross-product is checked with literal serialization oracles in unit tests.
// These real-boundary cases retain acceptance, refusal, focus guidance and RTL per language.
const outcomes = [
  ["REPLACE", "CLEAR", "CLEAR", null],
  ["KEEP", "REPLACE", "KEEP", "PAYMENT_ACKNOWLEDGEMENT_REQUIRED"],
] as const;
const expectAuthored = (bytes: Buffer, reference: string, modes: Modes, revision: number) => {
  const authored: unknown = JSON.parse(bytes.toString("utf8"));
  expect(authored).toMatchObject({
    caseReference: reference,
    expectedRevision: revision,
    act: {
      kind: "CORRECT",
      groups: {
        registration: { kind: modes.registration },
        decision: { kind: modes.decision },
        payment: { kind: modes.payment },
      },
    },
  });
};
const preserveChoice = async (page: Page, modes: Modes) => {
  const requests = trackRequests(page);
  for (const language of ["ar", "en", "lv", "en"] as const) {
    await selectLanguage(page, language);
    await expect(page.getByText(ui(language, "ui.correctionHint"), { exact: true })).toBeVisible();
    await expect(
      page.getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true }),
    ).toBeVisible();
    for (const group of ["registration", "decision", "payment"] as const) {
      const control = page.locator(`[name="act.groups.${group}.kind"]`);
      await expect(control).toHaveValue(modes[group]);
      await expect(control).toHaveAccessibleName(ui(language, "label.action"));
      await expect(control).toHaveAccessibleDescription(
        inputMessage(language, `group.${group}.meaning`),
      );
    }
    if (modes.registration === "REPLACE") {
      await expect(
        page.locator('input[name="act.groups.registration.values.claimedAmount"]'),
      ).toHaveValue("1.0000");
    }
  }
  expect(requests).toHaveLength(0);
};
const paidInStatus = async (page: Page, reference: string, status: "OPENED" | "CLOSED") => {
  await paidCase(page, reference, status);
  const before = await currentCase(page, reference);
  expect(before.revision).toBe(status === "CLOSED" ? 4 : 3);
  expect(before.fields.status).toBe(status);
  return before;
};
const expectPaymentGuidance = async (page: Page, refusal: string) => {
  if (refusal !== "PAYMENT_ACKNOWLEDGEMENT_REQUIRED") {
    return;
  }
  const payment = page.locator('[name="act.groups.payment.kind"]');
  await expect(payment).toBeFocused();
  await expect(payment).toHaveAttribute("aria-invalid", "true");
  await expect(payment).toHaveAttribute(
    "aria-describedby",
    "act.groups.payment.kind-meaning act.groups.payment.kind-error",
  );
  const description = page.locator('[id="act.groups.payment.kind-error"]');
  for (const [language, text] of [
    ["en", "explicit payment reaffirmation"],
    ["lv", "skaidri jāapstiprina"],
    ["ar", "إعادة تأكيد الدفع"],
  ] as const) {
    await selectLanguage(page, language);
    await expect(description).toContainText(text);
    await expect(payment).toHaveAccessibleName(ui(language, "label.action"));
    await expect(payment).toHaveAccessibleDescription(
      `${inputMessage(language, "group.payment.meaning")} ${inputMessage(language, "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED")}`,
    );
    await expect(payment).toHaveAttribute("aria-invalid", "true");
  }
  await payment.selectOption("CLEAR");
  await expect(payment).not.toHaveAttribute("aria-invalid");
  await expect(payment).toHaveAttribute("aria-describedby", "act.groups.payment.kind-meaning");
};
for (const status of ["CLOSED"] as const) {
  for (const language of ["en", "lv", "ar"] as const) {
    for (const [registration, decision, payment, refusal] of outcomes) {
      test(`submits literal CORRECT ${registration}/${decision}/${payment} in ${language} while ${status} with authoritative outcome`, async ({
        page,
      }) => {
        const reference = `GROUP-${randomUUID()}-A`;
        const modes = { registration, decision, payment };
        await openAuthenticated(page);
        const before = await paidInStatus(page, reference, status);
        await chooseGroups(page, modes, language);
        await preserveChoice(page, modes);
        await selectLanguage(page, language);
        const pending = await pauseJsonReply(page, "operation.preview");
        await page
          .getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true })
          .click();
        const captured = await pending.ready;
        expectAuthored(captured.bytes, reference, modes, before.revision);
        pending.release();
        if (refusal === null) {
          expect(captured.reply.outcome.kind).toBe("REVIEWED");
          await confirmReview(page);
          const recorded = page.waitForResponse(
            (response) => new URL(response.url()).pathname === "/api/operation.execute",
          );
          await page
            .getByRole("button", { name: ui(language, "ui.submitExact"), exact: true })
            .click();
          expect((await recorded).status()).toBe(200);
          await expect(page.locator("section.receipt")).toBeVisible();
          expectResultFields(before, await currentCase(page, reference), modes);
          const acceptedHistory = await history(page, reference);
          expect(acceptedHistory).toHaveLength(before.revision + 1);
          expect(
            acceptedHistory.some(
              (entry) => entry.kind === "FULL" && entry.receipt.subject.act === "CORRECT",
            ),
          ).toBe(true);
        } else {
          expect(captured.reply.outcome.kind).toBe("REJECTED");
          if (captured.reply.outcome.kind === "REJECTED") {
            expect(captured.reply.outcome.data.code).toBe(refusal);
          }
          await expect(page.getByRole("alert")).toBeVisible();
          await expectPaymentGuidance(page, refusal);
          expect(await currentCase(page, reference)).toEqual(before);
          expect(await history(page, reference)).toHaveLength(before.revision);
        }
      });
    }
  }
}
