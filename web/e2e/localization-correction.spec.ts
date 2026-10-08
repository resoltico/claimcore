import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import {
  pauseJsonReply,
  confirmPrepared,
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

// This complete published workflow includes three witnessed setup commits, language changes,
// correction and authoritative readback; allow its instrumented host a whole-workflow budget.
test.setTimeout(45_000);

// Independent paid-case outcomes; all replacement values actually change their groups.
const outcomes = [
  ["KEEP", "KEEP", "KEEP", "CORRECTION_NO_CHANGES"],
  ["KEEP", "KEEP", "REPLACE", null],
  ["KEEP", "KEEP", "CLEAR", null],
  ["KEEP", "REPLACE", "KEEP", "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"],
  ["KEEP", "REPLACE", "REPLACE", null],
  ["KEEP", "REPLACE", "CLEAR", null],
  ["KEEP", "CLEAR", "KEEP", "CORRECTION_PAYMENT_CLEAR_REQUIRED"],
  ["KEEP", "CLEAR", "REPLACE", "CORRECTION_PAYMENT_CLEAR_REQUIRED"],
  ["KEEP", "CLEAR", "CLEAR", null],
  ["REPLACE", "KEEP", "KEEP", null],
  ["REPLACE", "KEEP", "REPLACE", null],
  ["REPLACE", "KEEP", "CLEAR", null],
  ["REPLACE", "REPLACE", "KEEP", "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"],
  ["REPLACE", "REPLACE", "REPLACE", null],
  ["REPLACE", "REPLACE", "CLEAR", null],
  ["REPLACE", "CLEAR", "KEEP", "CORRECTION_PAYMENT_CLEAR_REQUIRED"],
  ["REPLACE", "CLEAR", "REPLACE", "CORRECTION_PAYMENT_CLEAR_REQUIRED"],
  ["REPLACE", "CLEAR", "CLEAR", null],
] as const;
const expectAuthored = (bytes: Buffer, reference: string, modes: Modes) => {
  const authored: unknown = JSON.parse(bytes.toString("utf8"));
  expect(authored).toMatchObject({
    caseReference: reference,
    expectedRevision: "3",
    command: {
      kind: "CORRECT_CASE",
      groups: {
        registration: { mode: modes.registration },
        decision: { mode: modes.decision },
        payment: { mode: modes.payment },
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
      page.getByRole("button", { name: ui(language, "ui.prepareExact"), exact: true }),
    ).toBeVisible();
    for (const [group, mode] of Object.entries(modes)) {
      await expect(page.locator(`#correction-${group}-mode`)).toHaveValue(mode);
    }
    if (modes.registration === "REPLACE") {
      await expect(page.locator('input[name="registration.claimedAmount"]')).toHaveValue("1.0000");
    }
  }
  expect(requests).toHaveLength(0);
};
for (const language of ["en", "lv", "ar"] as const) {
  for (const [registration, decision, payment, refusal] of outcomes) {
    test(`submits literal CORRECT_CASE ${registration}/${decision}/${payment} in ${language} with authoritative outcome [CC-DOM-002]`, async ({
      page,
    }) => {
      const reference = `GROUP-${randomUUID()}-A\u0308العربية`;
      const modes = { registration, decision, payment };
      await openAuthenticated(page);
      await paidCase(page, reference);
      const before = await currentCase(page, reference);
      expect(before.revision).toBe("3");
      await chooseGroups(page, modes, language);
      await preserveChoice(page, modes);
      await selectLanguage(page, language);
      const pending = await pauseJsonReply(page, "command.prepare");
      await page
        .getByRole("button", { name: ui(language, "ui.prepareExact"), exact: true })
        .click();
      const captured = await pending.ready;
      expectAuthored(captured.bytes, reference, modes);
      pending.release();
      if (refusal === null) {
        expect(captured.reply.outcome.tag).toBe("PREPARED");
        await confirmPrepared(page);
        const recorded = page.waitForResponse(
          (response) => new URL(response.url()).pathname === "/api/v3/operations/submit",
        );
        await page
          .getByRole("button", { name: ui(language, "ui.submitExact"), exact: true })
          .click();
        expect((await recorded).status()).toBe(200);
        await expect(page.locator("section.receipt")).toBeVisible();
        expectResultFields(before, await currentCase(page, reference), modes);
        const acceptedHistory = await history(page, reference);
        expect(acceptedHistory).toHaveLength(4);
        expect(
          acceptedHistory.some(
            (entry) => entry.tag === "FULL" && entry.receipt.command === "CORRECT_CASE",
          ),
        ).toBe(true);
      } else {
        expect(captured.reply.outcome.tag).toBe("REJECTED");
        if (captured.reply.outcome.tag === "REJECTED") {
          expect(captured.reply.outcome.data.rejection.diagnostic.id).toBe(refusal);
        }
        await expect(page.getByRole("alert")).toBeVisible();
        expect(await currentCase(page, reference)).toEqual(before);
        expect(await history(page, reference)).toHaveLength(3);
      }
    });
  }
}
