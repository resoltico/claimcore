import { documentActions } from "../src/operation/affordances";
import type { ResponseFor } from "@contracts/client";
import { acceptanceBanner, expectCaseReadsSettled, openAcceptedReceipt } from "./case-readiness";
import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import {
  caseActionButton,
  openCase,
  recordReviewed,
  reviewDraft,
  startAct,
  startOpen,
} from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { seedPresentation, selectLanguage, ui } from "./localization-support";
import { noHorizontalOverflow } from "./layout-support";
import { goToView } from "./workspace-navigation";

const generatedReference = /^CC-[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){3}-[0-9]{2}$/u;

test("automatic opening reviews pending assignment and records a copyable case reference", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await openAuthenticated(page);
  await startOpen(page);
  const custom = page.getByRole("checkbox", { name: "Use a custom case reference" });
  await expect(page.getByRole("textbox", { name: "Case reference", exact: true })).toHaveCount(0);
  await expect(custom).toHaveCount(0);
  await expect(page.getByText(/ClaimCore assigns its permanent reference/u)).toBeVisible();
  await expect(page.getByText(/Leave the case reference blank/u)).toHaveCount(0);
  const identity = await reviewDraft(page);
  const review = page.getByRole("dialog");
  await expect(review.getByText("Assigned when the case is opened.", { exact: true })).toHaveCount(
    1,
  );
  await expect(review.getByText(/Leave the case reference blank/u)).toHaveCount(0);
  await expect(review.getByText(/Nothing is recorded until you confirm/u)).toBeVisible();
  await expect(review.getByRole("button", { name: /Copy.*Case reference/u })).toHaveCount(0);
  await expect(review.getByRole("link", { name: /Case reference/u })).toHaveCount(0);
  await noHorizontalOverflow(page, "narrow");
  await recordReviewed(page);
  await expect(page).toHaveURL((url) => url.pathname === `/cases/id/${identity.operationId}`);
  await expect(page.locator("#case-detail-title")).toHaveText(generatedReference);
  const receipt = await openAcceptedReceipt(page);
  await expect(receipt.getByText(generatedReference, { exact: true }).first()).toBeVisible();
  await expect(
    receipt.getByRole("button", { name: /Copy.*Case reference/u }).first(),
  ).toBeVisible();
});
test("automatic opening recovers the issued reference after losing its acceptance reply", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await startOpen(page);
  const identity = await reviewDraft(page);

  const executions: string[] = [];
  context.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions.push((request.postDataJSON() as { operationId: string }).operationId);
    }
  });

  let issued: string | undefined;
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));
  await page.route("**/api/operation.execute", async (route) => {
    const committed = await route.fetch({ maxRedirects: 0 });
    expect(committed.status()).toBe(200);
    const reply = (await committed.json()) as ResponseFor<"operation.execute", "HUMAN">;
    expect(reply.operationId).toBe(identity.operationId);
    if (reply.outcome.kind !== "ACCEPTED") {
      throw new Error("The generated opening did not commit.");
    }
    const reference = reply.outcome.document.screen.regions
      .flatMap(({ content }) => (content.kind === "ENTRIES" ? content.entries : []))
      .find(({ key }) => key === "reference")?.value;
    if (reference?.kind !== "REFERENCE") {
      throw new Error("The opening receipt is missing its issued reference.");
    }
    issued = reference.value;
    expect(issued).toMatch(generatedReference);
    await route.abort("connectionfailed");
  });

  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeVisible();
  expect(issued).toBeDefined();

  const replacement = await context.newPage();
  try {
    const lookup = replacement
      .waitForResponse(
        (response) =>
          response.url().endsWith("/api/operation.get") &&
          response.ok() &&
          (response.request().postDataJSON() as { operationId: string }).operationId ===
            identity.operationId,
      )
      .then(async (response) => (await response.json()) as ResponseFor<"operation.get", "HUMAN">);
    await page.close();
    await openAuthenticated(replacement);
    expect(await lookup).toMatchObject({
      operationId: identity.operationId,
      outcome: {
        kind: "ACCEPTED",
        byRequester: true,
        document: {
          screen: {
            regions: expect.arrayContaining([
              expect.objectContaining({
                content: {
                  kind: "ENTRIES",
                  entries: expect.arrayContaining([
                    expect.objectContaining({
                      key: "reference",
                      value: { kind: "REFERENCE", value: issued },
                    }),
                  ]),
                },
              }),
            ]),
          },
        },
      },
    });
    await goToView(replacement, "recovery");
    await expect(replacement.locator('.unconfirmed-list [data-state="RECORDED"]')).toBeVisible();
    await replacement.getByRole("link", { name: "Open case", exact: true }).click();
    await expect(replacement).toHaveURL(
      (url) => url.pathname === `/cases/id/${identity.operationId}`,
    );
    await expect(replacement.locator("#case-detail-title")).toHaveText(issued!);
    expect(executions).toEqual([identity.operationId]);
  } finally {
    await replacement.close();
  }
});

test("protects exact routed amendment edits on Back while untouched drafts leave directly", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `DRAFT-${randomUUID().slice(0, 8)}`);
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
    page.getByRole("dialog").getByRole("heading", {
      name: "Close the case",
      exact: true,
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
  const selectedFacts = page.locator(".operation-form input[data-selects-fields]");
  await selectedFacts.first().waitFor({
    state: "visible",
  });
  for (const selected of await selectedFacts.all()) {
    await selected.check();
  }
  const fields = page.locator(".operation-form [data-field-name]");
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
  const reference = `RECEIPT-${randomUUID().slice(0, 8)}`;
  await startOpen(page, reference);
  await reviewDraft(page);
  await expect(page.locator(".operation-form")).toHaveCount(0);
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
  await expect(detail).toHaveText(generatedReference);
  const banner = acceptanceBanner(page);
  await expect(banner).toContainText("Case opened.");
  await expect(banner).toBeFocused();
  await expect(page.getByRole("dialog", { name: "View receipt", exact: true })).toHaveCount(0);
  const receipt = await openAcceptedReceipt(page);
  await expect(receipt).toContainText("Case opened.");
  await receipt.getByRole("button", { name: "Close", exact: true }).click();
  await expect(
    page
      .getByRole("navigation", { name: "Breadcrumb" })
      .getByRole("link", { name: "Cases", exact: true }),
  ).toBeVisible();
  await expect(page.locator("section.case-detail details[data-region=DETAILS]")).toHaveCount(0);
  await expect(page.locator(".operation-form")).toHaveCount(0);
  await expect(await caseActionButton(page, "case.amendRegistration", "en")).toBeVisible();
});

test("accepted receipt opens in a readable dialog in desktop LTR and narrow RTL", async ({
  page,
}, info) => {
  await seedPresentation(page, "en", "en-GB");
  await page.setViewportSize({ width: 1440, height: 1050 });
  await openAuthenticated(page);
  const reference = `RECEIPT-LAYOUT-${randomUUID().slice(0, 8)}`;
  const { caseReference } = await openCase(page, reference);
  for (const language of ["en", "ar"] as const) {
    if (language === "ar") {
      await page.setViewportSize({ width: 390, height: 844 });
      await selectLanguage(page, language);
      await expect(page.locator("#case-detail-title")).toBeVisible();
    }
    await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
    const receipt = await openAcceptedReceipt(page);
    await expect(receipt.locator('[data-entry="reference"] dd bdi')).toHaveText(caseReference);
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
