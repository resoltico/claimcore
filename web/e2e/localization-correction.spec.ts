import { expectCaseReadsSettled } from "./case-readiness";
import { choiceGroup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Locator, type Page } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import {
  pauseJsonReply,
  confirmReview,
  selectLanguage,
  ui,
  trackForegroundRequests,
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
  ["UPDATE", "UPDATE", "400.2500"],
  ["KEEP", "UPDATE", "200.2500"],
] as const;
const expectAuthored = (bytes: Buffer, caseId: string, modes: Modes, revision: number) => {
  const authored: unknown = JSON.parse(bytes.toString("utf8"));
  expect(authored).toMatchObject({
    task: "case.correct",
    binding: { target: { kind: "ID", caseId }, expectedRevision: revision },
    inputs: { "group:registration": modes.registration, "group:decision": modes.decision },
  });
};
const expectClaimantWarning = async (field: Locator, language: "en" | "lv" | "ar") => {
  const warning = {
    en: "Contains U+200D",
    lv: "Satur U+200D",
    ar: "يحتوي على \u2068U+200D\u2069",
  }[language];
  const authored = "A\u0308 العربية\u200D <tag>";
  await expect(field.locator(".character-warning")).toBeVisible();
  await expect(field.locator(".character-warning")).toHaveText(warning);
  await expect(field.getByText(authored, { exact: true })).toHaveJSProperty(
    "textContent",
    authored,
  );
};
const expectReviewedClaimantWarning = async (page: Page, language: "en" | "lv" | "ar") => {
  const field = page
    .getByRole("dialog")
    .locator(".review-values > div")
    .filter({
      has: page.getByText(ui(language, "field.policyNumber.label"), { exact: true }),
    });
  await expectClaimantWarning(field, language);
};
const expectRecordedClaimantWarning = async (page: Page, language: "en" | "lv" | "ar") => {
  await expectCaseReadsSettled(page);
  const field = page
    .locator(".case-detail")
    .locator('[data-region="FACTS"] [data-entry="policyNumber"]');
  await expectClaimantWarning(field, language);
};
// Literal widget-hint oracles remain independent of the browser's message lookup.
const correctionHint = {
  en: "Choose which details to change.",
  lv: "Izvēlieties maināmo informāciju.",
  ar: "اختر التفاصيل التي تريد تغييرها.",
} as const;
const preserveChoice = async (page: Page, modes: Modes) => {
  const requests = trackForegroundRequests(page);
  for (const language of ["ar", "en", "lv", "en"] as const) {
    await selectLanguage(page, language);
    await expect(
      page.getByText(ui(language, "label.correctionSelection"), { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true }),
    ).toBeVisible();
    for (const group of ["registration", "decision"] as const) {
      const control = choiceGroup(page, `group:${group}`);
      await expect(control.locator(`input[value="${modes[group]}"]`)).toBeChecked();
      await expect(control).toHaveAccessibleName(
        ui(
          language,
          group === "registration" ? "group.registration.label" : "group.decision.label",
        ),
      );
      await expect(control).toHaveAccessibleDescription(correctionHint[language]);
    }
    if (modes.registration === "UPDATE") {
      await expect(page.locator('input[name="field:claimedAmount"]')).toHaveValue("1.0000");
    }
  }
  expect(requests).toHaveLength(0);
};
const paidInStatus = async (page: Page, reference: string, status: "OPENED" | "CLOSED") => {
  await paidCase(page, reference, status);
  const before = await currentCase(page, reference);
  expect(before.revision).toBe(status === "CLOSED" ? 4 : 3);
  expect(before.fields.handling.kind).toBe(status === "CLOSED" ? "CLOSED" : "OPEN");
  return before;
};
for (const status of ["CLOSED"] as const) {
  for (const language of ["en", "lv", "ar"] as const) {
    for (const [registration, decision, payable] of outcomes) {
      test(`submits literal CORRECT ${registration}/${decision} ${payable} in ${language} while ${status} with authoritative outcome`, async ({
        page,
        browserName,
      }, testInfo) => {
        const reference = `GROUP-${randomUUID()}-A`;
        const modes = { registration, decision, payable };
        await openAuthenticated(page);
        const before = await paidInStatus(page, reference, status);
        await chooseGroups(page, modes, language);
        await preserveChoice(page, modes);
        await selectLanguage(page, language);
        if (browserName === "chromium" && language === "en" && registration === "UPDATE") {
          await page.setViewportSize({ width: 1440, height: 1000 });
          await choiceGroup(page, "group:registration").scrollIntoViewIfNeeded();
          await page.screenshot({ path: testInfo.outputPath("correction-groups-en-light.png") });
        }
        const pending = await pauseJsonReply(page, "operation.preview");
        await page
          .getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true })
          .click();
        const captured = await pending.ready;
        expectAuthored(captured.bytes, before.caseId, modes, before.revision);
        pending.release();
        expect(captured.reply.outcome.kind).toBe("REVIEWED");
        if (registration === "UPDATE") {
          await expectReviewedClaimantWarning(page, language);
        }
        if (browserName === "chromium" && language === "en") {
          await page.screenshot({ path: testInfo.outputPath("correction-review-en-light.png") });
        }
        await confirmReview(page);
        const recorded = page.waitForResponse(
          (response) => new URL(response.url()).pathname === "/api/operation.execute",
        );
        await page.locator("button[data-confirm-operation]").click();
        const response = await recorded;
        expect(response.status()).toBe(200);
        expect(response.request().postData()).toBe(captured.bytes.toString("utf8"));
        const accepted: unknown = await response.json();
        expect(accepted).toMatchObject({
          representation: "HUMAN",
          outcome: {
            kind: "ACCEPTED",
            document: { subject: { kind: "RECEIPT", operationId: captured.reply.operationId } },
          },
        });
        await expect(page.locator("#case-detail-title")).toBeVisible();
        await expect(page.locator(".accepted-receipt")).toBeVisible();
        if (registration === "UPDATE") {
          await expectRecordedClaimantWarning(page, language);
        }
        expectResultFields(before, await currentCase(page, reference), modes);
        const acceptedHistory = await history(page, reference);
        expect(acceptedHistory.filter((entry) => entry.subject.partKind === "CASE")).toHaveLength(
          before.revision + 1,
        );
        expect(
          acceptedHistory.filter((entry) => entry.subject.partKind === "PAYMENT"),
        ).toHaveLength(1);
        expect(
          acceptedHistory.some(
            (entry) => entry.subject.partKind === "CASE" && entry.task === "case.correct",
          ),
        ).toBe(true);
      });
    }
  }
}
