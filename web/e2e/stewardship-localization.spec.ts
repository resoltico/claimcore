import type { ResponseByEndpoint } from "@contracts/client";
import { showExactLookup } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openCase, startAct, reviewDraft, recordReviewed } from "./case-workflow";
import { workRequest } from "./work-workflow";
import { isObject } from "../src/protocol/client";
import {
  openAuthenticated,
  authenticateSteward,
  expectAccessible,
  browserRequest,
  sessionToken,
  installationId,
} from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  noHorizontalOverflow,
  expectKeyboardContained,
  ui,
} from "./localization-support";
let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});
test("stewardship review preserves exact reason through pseudolocale, Arabic RTL and narrow keyboard interaction @engine", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `STEWARD-LOCALE-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", stewardCookies);
    const lookup = await showExactLookup(steward);
    await lookup.fill(reference);
    await lookup.press("Enter");
    await steward.locator("details.case-actions > summary").click();
    await steward
      .getByRole("button", {
        name: /Vööïïd/u,
      })
      .click();
    const reason = "Exact 1.0000 العربية";
    await steward.locator('[name="input:reason"]').fill(reason);
    const preview = steward.getByRole("dialog").locator("button[type=submit]");
    const previewed = steward.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/operation.preview",
    );
    await preview.click();
    const response = await previewed;
    const operationPreviewReply =
      (await response.json()) as ResponseByEndpoint["operation.preview"];
    if (operationPreviewReply.outcome.kind !== "REVIEWED") {
      throw new Error("Expected the real reviewed stewardship draft.");
    }
    const authored: unknown = JSON.parse(response.request().postData() ?? "null");
    expect(authored).toMatchObject({ task: "case.void", inputs: { "input:reason": reason } });
    if (
      typeof authored !== "object" ||
      authored === null ||
      !("operationId" in authored) ||
      typeof authored.operationId !== "string"
    ) {
      throw new Error("Missing frozen stewardship identity.");
    }
    const { operationId } = authored;
    const dialog = steward.getByRole("dialog");
    await expect(dialog).toContainText(reason);
    await expect(
      dialog.getByRole("heading", {
        name: /Vööïïd/u,
      }),
    ).toBeVisible();
    await steward.setViewportSize({
      width: 320,
      height: 720,
    });
    await noHorizontalOverflow(steward, "narrow");
    await expectKeyboardContained(steward, dialog);
    const requests: string[] = [];
    steward.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/operation.execute") {
        requests.push(request.postData() ?? "");
      }
    });
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAccessibleName(ui(language, "task.case.void.label"));
      await expect(dialog.locator("button[data-confirm-operation]")).toBeEnabled();
      await expect(dialog.locator(".screen-details")).toContainText(operationId);
      await expect(dialog).toContainText(reason);
      await noHorizontalOverflow(steward, "narrow");
    }
    const lifecycle = dialog.locator(".review-values > div").filter({
      has: steward.getByText("Lifecycle", {
        exact: true,
      }),
    });
    await expect(
      lifecycle.locator(".review-before").getByText("Active", {
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      lifecycle.locator(".review-after").getByText("Voided", {
        exact: true,
      }),
    ).toBeVisible();
    expect(requests).toEqual([]);
    await expectAccessible(steward);
    await steward.keyboard.press("Escape");
    const guard = steward.getByRole("dialog");
    await expect(guard).toContainText("Discard this draft and leave the editor?");
    await guard
      .getByRole("button", {
        name: "Keep editing",
        exact: true,
      })
      .click();
    await expect(dialog).toContainText(reason);
    await expect(dialog.locator(".screen-details")).toContainText(operationId);
    await dialog
      .getByRole("button", {
        name: "Return to editing",
        exact: true,
      })
      .click();
    await expect(preview).toBeFocused();
    await expect(steward.locator('[name="input:reason"]')).toHaveValue(reason);
    expect(requests).toEqual([]);
  } finally {
    await context.close();
  }
});

const hasExactReason = (value: unknown, reason: string): boolean => {
  if (typeof value === "string") {
    return value === reason;
  }
  if (Array.isArray(value)) {
    return value.some((item: unknown) => hasExactReason(item, reason));
  }
  return isObject(value) && Object.values(value).some((item) => hasExactReason(item, reason));
};

test("accepted void and reinstatement reasons remain historical in actual receipt and DATA/HUMAN history @engine", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `ACCEPTED-REASONS-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en", "en-GB");
    await openAuthenticated(steward, "Cases", stewardCookies);
    const lookup = await showExactLookup(steward);
    await lookup.fill(reference);
    await lookup.press("Enter");
    const accepted: { operationId: string; reason: string }[] = [];
    for (const [task, reason] of [
      ["case.void", "Accepted void A 1.0000 العربية <tag>"],
      ["case.reinstate", "Accepted reinstate B 2.0000 日本語"],
      ["case.void", "Accepted void C 3.0000 Ā"],
    ] as const) {
      await startAct(steward, task, "en");
      await steward.locator('[name="input:reason"]').fill(reason);
      const reviewed = await reviewDraft(steward);
      const delivered = steward
        .waitForResponse(
          (response) => new URL(response.url()).pathname === "/api/operation.execute",
        )
        .then(async (response) => {
          expect(response.status()).toBe(200);
          const body: unknown = await response.json();
          return body;
        });
      await recordReviewed(steward);
      const body = await delivered;
      expect(hasExactReason(body, reason)).toBe(true);
      accepted.push({ operationId: reviewed.operationId, reason });
      await expect(steward.locator(".accepted-receipt")).toContainText(reason);
    }
    await steward.setViewportSize({ width: 390, height: 844 });
    await selectLanguage(steward, "ar");
    await expect(steward.locator(".accepted-receipt")).toContainText(accepted[2]!.reason);
    await noHorizontalOverflow(steward, "narrow");
    const csrf = await sessionToken(page);
    const installation = await installationId(page, csrf);
    const human = async (endpoint: string, input: unknown) => {
      const response = await browserRequest(page, `/api/${endpoint}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-ClaimCore-Antiforgery": csrf,
          "ClaimCore-Installation": installation,
          "ClaimCore-Representation": "HUMAN",
        },
        body: JSON.stringify(input),
      });
      expect(response.status).toBe(200);
      return response.payload;
    };
    for (const entry of accepted) {
      const data = await workRequest(page, "operation.get", { operationId: entry.operationId });
      const shown = await human("operation.get", { operationId: entry.operationId });
      for (const value of [data, shown]) {
        expect(hasExactReason(value, entry.reason)).toBe(true);
        for (const other of accepted.filter(
          (candidate) => candidate.operationId !== entry.operationId,
        )) {
          expect(hasExactReason(value, other.reason)).toBe(false);
        }
      }
    }
    const target = { kind: "REFERENCE", caseReference: reference } as const;
    const dataHistory = await workRequest(page, "case.timeline", { target, limit: 20 });
    expect(dataHistory.outcome.kind).toBe("SUCCEEDED");
    const humanHistory = await human("case.timeline", { target, limit: 20 });
    for (const entry of accepted) {
      expect(hasExactReason(dataHistory, entry.reason)).toBe(true);
      expect(hasExactReason(humanHistory, entry.reason)).toBe(true);
    }
    await expectAccessible(steward);
  } finally {
    await context.close();
  }
});
