import { type ResponseFor } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { showCaseRecordDetails } from "./workspace-navigation";
import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase, reviewDraft, startAct, startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";
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
  await expect(fields).toHaveCount(10);
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
  if (caseGetReply.representation !== "HUMAN" || caseGetReply.outcome.kind !== "SUCCEEDED") {
    throw new Error("E2E_ACCEPTED_OPEN_CASE_DISCLOSURE_REQUIRED");
  }
  expect(caseGetReply.outcome.document.context.revision).toBe(1);
  expect(caseGetReply.outcome.document.context.tasks).toContain("case.amendRegistration");
  await expect(page.locator('[data-entry="caseReference"]').first()).toContainText(reference);
  const detail = page.locator("#case-detail-title");
  await expect(detail).toBeVisible();
  await expect(detail).toBeFocused();
  await expect(page.locator(".success-toast")).toContainText("Case opened.");
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
  await expect(page.locator("section.success-toast.receipt")).toHaveCount(1);
  await expect(page.locator("section.receipt:not(.success-toast)")).toHaveCount(0);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.locator('button[data-act="case.amendRegistration"]')).toHaveCount(1);
});
