import { documentActions } from "../src/operation/affordances";
import type { ResponseFor } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { showCaseRecordDetails } from "./workspace-navigation";
import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, reviewDraft, startAct, startOpen } from "./case-workflow";
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
  await expect(page.locator('[data-entry="caseReference"]').first()).toContainText(reference);
  const detail = page.locator("#case-detail-title");
  await expect(detail).toBeVisible();
  await expect(detail).toBeFocused();
  await expect(page.locator(".accepted-receipt")).toContainText("Case opened.");
  await showCaseRecordDetails(page);
  await expect(
    page.locator(
      "section.case-detail > .screen-view > [data-region=DETAILS] .entries-view [data-entry='revision'] dd",
    ),
  ).toContainText("1");
  await expect(page.locator(".operation-form-card")).toHaveCount(0);
  await expect(
    page.getByRole("button", {
      name: "Back to cases",
      exact: true,
    }),
  ).toBeVisible();
  await expect(page.locator("section.accepted-receipt.receipt")).toHaveCount(1);
  await expect(page.locator("section.receipt:not(.accepted-receipt)")).toHaveCount(0);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.locator('button[data-act="case.amendRegistration"]')).toHaveCount(1);
});

test("accepted receipt fills its content width and keeps dismissal above facts in desktop LTR and narrow RTL", async ({
  page,
}, info) => {
  await seedPresentation(page, "en", "en-GB");
  await page.setViewportSize({ width: 1440, height: 1050 });
  await openAuthenticated(page);
  const reference = `RECEIPT-LAYOUT-${randomUUID()}`;
  await openCase(page, reference);
  const receipt = page.locator("section.receipt");
  await expect(receipt.locator('[data-region="DETAILS"]')).toBeVisible();
  for (const language of ["en", "ar"] as const) {
    if (language === "ar") {
      await page.setViewportSize({ width: 390, height: 844 });
      await selectLanguage(page, language);
      await expect(receipt.locator('[data-region="DETAILS"]')).toBeVisible();
    }
    await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
    await expect(receipt.locator('[data-entry="reference"] dd')).toHaveText(reference);
    await expect(receipt.locator('[data-entry="policyNumber"] dd')).toHaveText(
      "Synthetic claimant",
    );
    await expect(receipt.locator('[data-entry="insurerClaimReference"] dd')).toHaveText(
      "Synthetic insurer",
    );
    await expect(receipt.locator('[data-entry="claimedAmount"] dd')).toHaveText("1,200.50 EUR");
    const close = receipt.getByRole("button", { name: ui(language, "ui.close"), exact: true });
    const geometry = await receipt.evaluate(
      (element, closeLabel) => {
        const screen = element.querySelector(":scope > .screen-view")!;
        const button = [...element.querySelectorAll("button")].find(
          (candidate) => candidate.textContent?.trim() === closeLabel,
        )!;
        const heading = element.querySelector('[data-region="IDENTITY"] .region-heading')!;
        const rect = (node: Element) => {
          const { top, bottom, left, width, height } = node.getBoundingClientRect();
          return { top, bottom, left, width, height };
        };
        const style = getComputedStyle(element);
        return {
          usableWidth:
            element.clientWidth -
            parseFloat(style.paddingInlineStart) -
            parseFloat(style.paddingInlineEnd),
          screen: rect(screen),
          close: rect(button),
          heading: rect(heading),
          receipt: rect(element),
        };
      },
      ui(language, "ui.close"),
    );
    await info.attach(`receipt-${language}-geometry`, {
      body: JSON.stringify(geometry, null, 2),
      contentType: "application/json",
    });
    expect
      .soft(geometry.screen.width, `${language}: full Screen uses the receipt's usable width`)
      .toBeGreaterThanOrEqual(geometry.usableWidth - 1);
    expect
      .soft(geometry.close.bottom, `${language}: dismissal precedes the full receipt Screen`)
      .toBeLessThanOrEqual(geometry.screen.top + 1);
    expect
      .soft(geometry.close.top, `${language}: dismissal stays above the result heading`)
      .toBeLessThan(geometry.heading.bottom);
    await close.focus();
    await expect(close).toBeFocused();
    await noHorizontalOverflow(page, "narrow");
    await expectAccessible(page);
    await page.screenshot({ path: info.outputPath(`receipt-layout-${language}.png`) });
  }
  await receipt.getByRole("button", { name: ui("ar", "ui.close"), exact: true }).press("Enter");
  await expect(receipt).toHaveCount(0);
  await expect(page.locator("#case-detail-title")).toBeVisible();
});
