import { goToView } from "./workspace-navigation";
import { inputMessage } from "./input-accessibility-messages";
import { expectInputRefusal } from "./input-refusal-expectations";
import { isResponse, isRequest } from "../src/protocol/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import {
  openAuthenticated,
  expectAccessible,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import { syntheticPrincipals } from "./authority-setup";
import { trackForegroundRequests } from "./localization-support";
import { recoveryRecords } from "./recovery-storage";

const operationId = (value: unknown): string => {
  if (!isRequest("operation.preview", value)) {
    throw new Error("Expected the exact authored preview request.");
  }
  return value.operationId;
};

test("context interruption recovers through an explicit in-page read retry", async ({ page }) => {
  await openAuthenticated(page);
  await page.route("**/api/context", (route) => route.abort("connectionfailed"));
  await page.reload();
  await expect(page.getByRole("button", { name: "Try again", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Cases", exact: true })).toHaveCount(0);
  await page.unroute("**/api/context");
  await page.getByRole("button", { name: "Try again", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  await expectAccessible(page);
});

test("keyboard navigation focuses the editor and restores its invoking control on return", async ({
  page,
}) => {
  await openAuthenticated(page);
  const open = page.getByRole("link", { name: "Open a case", exact: true });
  await open.focus();
  await page.keyboard.press("Enter");
  const editor = page.getByRole("dialog", { name: "Open a case", exact: true });
  await expect(editor).toBeVisible();
  expect(await editor.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  await editor.getByRole("button", { name: "Close", exact: true }).first().focus();
  await page.keyboard.press("Enter");
  await expect(editor).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  await expect(open).toBeFocused();
});

test("authority refusal preserves authored input through Settings and needs a fresh review and explicit consent", async ({
  page,
}) => {
  await openAuthenticated(page);
  await goToView(page, "people");
  await expect(page.locator("#authority-title")).toBeVisible();
  // This raw fixture is a separate client; let the app's bounded People/bootstrap reads settle.
  await page.waitForLoadState("networkidle");
  const source = await syntheticPrincipals();
  const subjectValue = `repair-${randomUUID()}`;
  const profileName = "Authored repair candidate";
  const executions: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions.push(request.postData() ?? "");
    }
  });
  // The sole issuer is fixed in the UI. Preserve the original empty-issuer boundary through a real API preview.
  const rawIssuer = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      kind: "AUTHORITY",
      operationId: randomUUID(),
      target: { kind: "HUMAN", issuer: "", subject: subjectValue },
      act: {
        kind: "ADMIT",
        profile: { displayName: profileName, contact: null },
        grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
      },
    }),
  });
  if (rawIssuer.status !== 200) {
    throw new Error(`E2E_WORK_READBACK_HTTP_${rawIssuer.status}_RAW_ISSUER_PREVIEW`);
  }
  expect(rawIssuer.status).toBe(200);
  expect(rawIssuer.payload).toMatchObject({
    outcome: {
      kind: "REJECTED",
      data: {
        code: "INVALID_INPUT",
        action: "CORRECT_INPUT",
        issues: [{ field: "principal.issuer", code: "TEXT_REQUIRED" }],
      },
    },
  });
  const requests = trackForegroundRequests(page);
  await page.getByRole("button", { name: "Add person", exact: true }).first().click();
  const editor = page.getByRole("dialog", { name: "Add person", exact: true });
  const issuer = editor.locator('[name="target.issuer"]');
  const subject = editor.locator('[name="target.subject"]');
  const profile = editor.locator('[name="act.profile.displayName"]');
  await expect(issuer).toHaveValue(source.issuer);
  await expect(issuer).toBeDisabled();
  await profile.fill(profileName);
  await subject.fill(subjectValue);
  await editor.locator('input[name$=".role"][value="CASE_READER"]').check();
  await subject.fill("");
  const first = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await editor.getByRole("button", { name: "Review changes", exact: true }).click();
  const response = await first;
  expect(response.status()).toBe(200);
  const reply: unknown = await response.json();
  if (!isResponse("operation.preview", reply) || reply.outcome.kind !== "REJECTED") {
    throw new Error("Expected an Application subject refusal.");
  }
  expect(reply.outcome.data).toEqual({
    code: "INVALID_INPUT",
    action: "CORRECT_INPUT",
    issues: [{ field: "principal.subject", code: "TEXT_REQUIRED" }],
  });
  await expectInputRefusal(page, subject, "en", {
    meaning: inputMessage("en", "field.principal.subject.hint"),
    issue: "A non-blank value is required.",
  });
  await expect(editor.locator("[data-error-summary]")).toBeFocused();
  await editor.locator("[data-error-summary]").getByRole("link").click();
  await expect(subject).toBeFocused();
  await expect(profile).toHaveValue(profileName);
  await expect(editor.locator("button[data-confirm-operation]")).toHaveCount(0);
  const refused: unknown = response.request().postDataJSON();
  expect(refused).toEqual({
    kind: "AUTHORITY",
    operationId: operationId(refused),
    target: { kind: "HUMAN", issuer: source.issuer, subject: "" },
    act: {
      kind: "ADMIT",
      profile: { displayName: profileName },
      grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
    },
  });
  await subject.fill(subjectValue);
  await expect(subject).not.toHaveAttribute("aria-invalid");
  await expect(editor.locator("button[data-confirm-operation]")).toHaveCount(0);
  await editor.locator("[data-open-settings]").click();
  await expect(page).toHaveURL((url) => url.pathname === "/settings");
  await expect(page.locator(".settings-panel")).toBeVisible();
  await page.locator("[data-return-workspace]").click();
  await expect(editor).toBeVisible();
  await expect(subject).toHaveValue(subjectValue);
  await expect(profile).toHaveValue(profileName);
  expect(requests).toEqual(["POST /api/operation.preview"]);
  const next = page.waitForResponse((value) => value.url().endsWith("/api/operation.preview"));
  await editor.getByRole("button", { name: "Review changes", exact: true }).click();
  const reviewedResponse = await next;
  const reviewed: unknown = await reviewedResponse.json();
  if (!isResponse("operation.preview", reviewed) || reviewed.outcome.kind !== "REVIEWED") {
    throw new Error("Expected a fresh authority review after correction.");
  }
  const corrected: unknown = reviewedResponse.request().postDataJSON();
  expect(corrected).toEqual({
    kind: "AUTHORITY",
    operationId: operationId(corrected),
    target: { kind: "HUMAN", issuer: source.issuer, subject: subjectValue },
    act: {
      kind: "ADMIT",
      profile: { displayName: profileName },
      grants: [{ role: "CASE_READER", scope: { kind: "INSTALLATION" } }],
    },
  });
  const correctedId = operationId(corrected);
  expect(correctedId).not.toBe(operationId(refused));
  const consent = editor.locator("button[data-confirm-operation]");
  await expect(consent).toHaveAccessibleName("Add person");
  await expect(consent).toBeEnabled();
  await expect(editor.getByRole("checkbox")).toHaveCount(0);
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
  expect(executions).toEqual([]);
  // A reviewed draft survives the Settings detour without an automatic read or write.
  await editor.locator("[data-open-settings]").click();
  await expect(page.locator(".settings-panel")).toBeVisible();
  await page.locator("[data-return-workspace]").click();
  await expect(consent).toBeEnabled();
  await expect(editor.locator(".review-request")).toContainText(correctedId);
  expect(requests).toEqual(["POST /api/operation.preview", "POST /api/operation.preview"]);
  await editor.getByRole("button", { name: "Return to editing", exact: true }).click();
  const repeated = page.waitForResponse((value) => value.url().endsWith("/api/operation.preview"));
  await editor.getByRole("button", { name: "Review changes", exact: true }).click();
  const repeatedResponse = await repeated;
  const repeatedReply: unknown = await repeatedResponse.json();
  const reviewedBytes = reviewedResponse.request().postDataBuffer();
  expect(reviewedBytes).not.toBeNull();
  expect(repeatedResponse.request().postDataBuffer()).toEqual(reviewedBytes);
  if (!isResponse("operation.preview", repeatedReply)) {
    throw new Error("Expected a typed repeated review.");
  }
  expect(repeatedReply.outcome.kind).toBe("REVIEWED");
  expect(requests).toEqual([
    "POST /api/operation.preview",
    "POST /api/operation.preview",
    "POST /api/operation.preview",
  ]);
  expect(executions).toEqual([]);
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
  await expectAccessible(page);
});
