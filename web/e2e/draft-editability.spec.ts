import { documentActions } from "../src/operation/affordances";
import type { ResponseFor } from "@contracts/client";
import { acceptanceBanner, expectCaseReadsSettled, openAcceptedReceipt } from "./case-readiness";
import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { caseActionButton, openCase, reviewDraft, startAct, startOpen } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { seedPresentation, selectLanguage, ui } from "./localization-support";
import { noHorizontalOverflow } from "./layout-support";
test("protects exact routed amendment edits on Back while untouched drafts leave directly", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `DRAFT-${randomUUID()}`);
  await startAct(page, "case.amendRegistration", "en");
  await expect(
    page.getByRole("combobox", {
      name: "Case action",
      exact: true,
    }),
  ).toHaveCount(0);
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await startAct(page, "case.close", "en");
  await expect(
    page.getByRole("heading", {
      name: "Close the case",
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toHaveCount(0);
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await startAct(page, "case.amendRegistration", "en");
  const selectedFacts = page.locator(".operation-form-card input[data-selects-fields]");
  await selectedFacts.first().waitFor({
    state: "visible",
  });
  for (const selected of await selectedFacts.all()) {
    await selected.check();
  }
  const fields = page.locator(".operation-form-card [data-field-name]");
  await expect(fields).toHaveCount(8);
  await expect(
    page.getByRole("textbox", {
      name: "Incident description",
      exact: true,
    }),
  ).toBeVisible();
  for (const name of Object.keys(registration())) {
    await fillBusinessField(page, name, "");
  }
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await expect(
    page.getByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toBeVisible();
  await page
    .getByRole("button", {
      name: "Keep editing",
    })
    .click();
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await expect(
    page.getByRole("dialog", {
      name: "Discard these draft changes?",
    }),
  ).toBeVisible();
  await page
    .getByRole("dialog", {
      name: "Discard these draft changes?",
    })
    .getByRole("button", {
      name: "Discard and leave",
      exact: true,
    })
    .click();
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.locator('.entries-view [data-entry="claimedAmount"] bdi').first()).toHaveText(
    "1,200.50 EUR",
  );
  await startAct(page, "case.amendRegistration", "en");
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await expect(page.locator("#case-detail-title")).toBeVisible();
});
test("review blocks authoring and accepted OPEN returns to the recorded case with its catalog completion notice", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `RECEIPT-${randomUUID()}`;
  await startOpen(page, reference);
  await reviewDraft(page);
  await expect(page.locator(".operation-form-card")).toHaveCount(0);
  await expect(page.getByRole("dialog").getByRole("textbox")).toHaveCount(0);
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toBeEnabled();
  const disclosed = page.waitForResponse(async (response) => {
    if (new URL(response.url()).pathname !== "/api/case.get" || response.status() !== 200) {
      return false;
    }
    const caseGetReply = (await response.json()) as ResponseFor<"case.get", "HUMAN">;
    return caseGetReply.representation === "HUMAN" && caseGetReply.outcome.kind === "SUCCEEDED";
  });
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expectCaseReadsSettled(page);
  const caseGetReply = (await (await disclosed).json()) as ResponseFor<"case.get", "HUMAN">;
  if (
    caseGetReply.representation !== "HUMAN" ||
    caseGetReply.outcome.kind !== "SUCCEEDED" ||
    caseGetReply.outcome.document.subject.kind !== "CASE"
  ) {
    throw new Error("E2E_ACCEPTED_OPEN_CASE_DISCLOSURE_REQUIRED");
  }
  expect(caseGetReply.outcome.document.subject.revision).toBe(1);
  expect(documentActions(caseGetReply.outcome.document).map((action) => action.task)).toContain(
    "case.amendRegistration",
  );
  const detail = page.locator("#case-detail-title");
  await expect(detail).toHaveText(reference);
  const banner = acceptanceBanner(page);
  await expect(banner).toContainText("Case opened.");
  await expect(banner).toBeFocused();
  await expect(page.locator(".page > .status-banner[data-tone=success]")).toHaveCount(0);
  const receipt = await openAcceptedReceipt(page);
  await expect(receipt).toContainText("Case opened.");
  await receipt.getByRole("button", { name: "Close", exact: true }).click();
  await expect(
    page
      .getByRole("navigation", { name: "Breadcrumb" })
      .getByRole("link", { name: "Cases", exact: true }),
  ).toBeVisible();
  await expect(page.locator("section.case-detail details[data-region=DETAILS]")).toHaveCount(0);
  await expect(page.locator(".operation-form-card")).toHaveCount(0);
  await expect(await caseActionButton(page, "case.amendRegistration", "en")).toBeVisible();
});

test("accepted receipt opens in a readable dialog in desktop LTR and narrow RTL", async ({
  page,
}, info) => {
  await seedPresentation(page, "en", "en-GB");
  await page.setViewportSize({ width: 1440, height: 1050 });
  await openAuthenticated(page);
  const reference = `RECEIPT-LAYOUT-${randomUUID()}`;
  await openCase(page, reference);
  for (const language of ["en", "ar"] as const) {
    if (language === "ar") {
      await page.setViewportSize({ width: 390, height: 844 });
      await selectLanguage(page, language);
      await expect(page.locator("#case-detail-title")).toBeVisible();
    }
    await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
    const receipt = await openAcceptedReceipt(page);
    await expect(receipt.locator('[data-entry="reference"] dd')).toHaveText(reference);
    await expect(receipt.locator('[data-entry="policyNumber"] dd')).toHaveText(
      "Synthetic claimant",
    );
    await expect(receipt.locator('[data-entry="insurerClaimReference"] dd')).toHaveText(
      "Synthetic insurer",
    );
    await expect(receipt.locator('[data-entry="claimedAmount"] dd')).toHaveText("1,200.50 EUR");
    await expect
      .poll(() => receipt.evaluate((element) => element.contains(document.activeElement)))
      .toBe(true);
    await noHorizontalOverflow(page, "narrow");
    await expectAccessible(page);
    await page.screenshot({ path: info.outputPath(`receipt-layout-${language}.png`) });
    await receipt
      .getByRole("button", { name: ui(language, "ui.close"), exact: true })
      .press("Enter");
    await expect(receipt).toHaveCount(0);
  }
  await expect(page.locator("#case-detail-title")).toBeVisible();
});
