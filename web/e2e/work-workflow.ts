import { chooseOption, settlePopupTriggerScroll } from "./workspace-navigation";
import { ui } from "./localization-support";
import type { ReviewIdentity } from "./case-workflow";
import { expect, type Page, type Locator, type Request } from "@playwright/test";
import { endpoints, isRequest, isResponseFor, type RequestByEndpoint } from "@contracts/client";
import { browserRequest, installationId, sessionToken } from "./session-helpers";
import { retainResponseBodies } from "./response-bodies";
import { isHostFailure } from "../src/protocol/client";
import { expectCaseReadsSettled } from "./case-readiness";

export const workConfirmButton = (page: Page) =>
  page.getByRole("dialog").locator("button[data-confirm-operation]");

/** Reveal the existing native disclosure before choosing a non-default case reminder view. */
export const selectCaseReminderState = async (
  page: Page,
  state: "PENDING" | "COMPLETED" | "CANCELLED",
): Promise<void> => {
  const controls = page.locator(".case-reminder-controls");
  if ((await controls.getAttribute("open")) === null) {
    await controls.locator(":scope > summary").click();
  }
  await chooseOption(page, controls.getByRole("combobox"), state);
};

const languageOf = async (page: Page) => {
  const language = await page.locator("html").getAttribute("lang");
  if (language !== "en" && language !== "lv" && language !== "ar") {
    throw new Error("E2E_WORK_LANGUAGE_UNAVAILABLE");
  }
  return language;
};

/** Circle opening requests one read-only preview; this helper never confirms or executes it. */
export const reviewReminderCompletion = async (
  page: Page,
  row: Locator,
): Promise<ReviewIdentity> => {
  await retainResponseBodies(page);
  const calls: string[] = [];
  const observed = (request: Request) => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/operation.preview" || path === "/api/operation.execute") {
      calls.push(path);
    }
  };
  page.on("request", observed);
  try {
    const response = page.waitForResponse(
      (value) => new URL(value.url()).pathname === "/api/operation.preview",
    );
    const circle = row.getByRole("button", {
      name: ui(await languageOf(page), "act.REMINDER_COMPLETE.label"),
      exact: false,
    });
    const originId = await circle.getAttribute("id");
    await circle.click();
    const delivered = await response;
    const input: unknown = delivered.request().postDataJSON();
    const payload: unknown = await delivered.json();
    if (
      delivered.status() !== 200 ||
      !isRequest("operation.preview", input) ||
      input.kind !== "REMINDER" ||
      input.act.kind !== "COMPLETE" ||
      !isResponseFor("operation.preview", "HUMAN", payload) ||
      payload.outcome.kind !== "REVIEWED"
    ) {
      throw new Error("E2E_REMINDER_COMPLETION_REVIEW_UNAVAILABLE");
    }
    expect(payload.operationId).toBe(input.operationId);
    expect(originId).toBe(`reminder-${input.act.reminderId}-COMPLETE`);
    await expect(page.locator(`[id="${originId}"]`)).toHaveCount(1);
    await expect(workConfirmButton(page)).toHaveCount(1);
    await expect(workConfirmButton(page)).toBeVisible();
    await expect(workConfirmButton(page)).toBeEnabled();
    await expect(workConfirmButton(page)).toHaveText(
      ui(await languageOf(page), "act.REMINDER_COMPLETE.label"),
    );
    expect(calls).toEqual(["/api/operation.preview"]);
    return { operationId: input.operationId };
  } finally {
    page.off("request", observed);
  }
};

/** Select only the supplied overflow action; review and confirmation remain separate steps. */
export const selectReminderAction = async (
  page: Page,
  row: Locator,
  kind: "UPDATE" | "CANCEL" | "REOPEN" | "COMPLETE_AND_PLAN_NEXT",
) => {
  const language = await languageOf(page);
  if (kind === "UPDATE") {
    await row.locator('button[id$="-UPDATE"]').click();
    return;
  }
  const trigger = row.getByRole("button", {
    name: ui(language, "label.availableActs"),
    exact: false,
  });
  await settlePopupTriggerScroll(trigger);
  await trigger.click();
  const menu = page.getByRole("menu", { name: ui(language, "label.availableActs"), exact: false });
  await expect(menu).toHaveCount(1);
  await expect(menu).toBeVisible();
  await menu
    .getByRole("menuitem", { name: ui(language, `act.REMINDER_${kind}.label`), exact: true })
    .click();
};

const acceptedWorkSubmission = (family: "WORK" | "REMINDER", input: unknown, payload: unknown) => {
  if (
    !isRequest("operation.execute", input) ||
    (input.kind !== "WORK" && input.kind !== "REMINDER") ||
    input.kind !== family
  ) {
    throw new Error("E2E_WORK_SUBMIT_INPUT_MISMATCH");
  }
  if (
    !isResponseFor("operation.execute", "HUMAN", payload) ||
    (payload.outcome.kind !== "ACCEPTED" && payload.outcome.kind !== "ALREADY_ACCEPTED") ||
    payload.outcome.document.context.target.kind !== family
  ) {
    throw new Error("E2E_WORK_SUBMIT_FAILED");
  }
  return { input, envelope: payload.operationId, receipt: payload.outcome.document };
};

export const workRequest = async <K extends keyof RequestByEndpoint>(
  page: Page,
  endpoint: K,
  body: RequestByEndpoint[K],
) => {
  const token = await sessionToken(page);
  const entry = endpoints.find((value) => value.id === endpoint)!;
  const result = await browserRequest(page, entry.path, {
    method: entry.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
      ...(entry.requiresInstallation
        ? { "ClaimCore-Installation": await installationId(page, token) }
        : {}),
    },
    body: JSON.stringify(body),
  });
  if (result.status !== 200 || !isResponseFor(endpoint, "DATA", result.payload)) {
    const detail = isHostFailure(result.payload, result.status)
      ? result.payload.hostFailure.code
      : "INVALID_RESPONSE";
    throw new Error(`E2E_WORK_READBACK_HTTP_${result.status}_${detail}`);
  }
  return result.payload;
};

export const recordWorkReview = async (page: Page, family: "WORK" | "REMINDER") => {
  await retainResponseBodies(page);
  const language = await languageOf(page);
  const confirmed = workConfirmButton(page);
  await expect(confirmed).toHaveCount(1);
  await expect(confirmed).toBeVisible();
  await expect(confirmed).toBeEnabled();
  const label = (await confirmed.textContent())?.trim();
  const labelKeys =
    family === "WORK"
      ? (["act.UPDATE_WORK.label"] as const)
      : ([
          "act.REMINDER_CREATE.label",
          "act.REMINDER_UPDATE.label",
          "act.REMINDER_COMPLETE.label",
          "act.REMINDER_CANCEL.label",
          "act.REMINDER_REOPEN.label",
          "act.REMINDER_COMPLETE_AND_PLAN_NEXT.label",
        ] as const);
  expect(labelKeys.map((key) => ui(language, key))).toContain(label);
  const response = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.execute",
  );
  await confirmed.click();
  const delivered = await response;
  const payload: unknown = await delivered.json();
  expect(delivered.status()).toBe(200);
  const { input, envelope, receipt } = acceptedWorkSubmission(
    family,
    delivered.request().postDataJSON(),
    payload,
  );
  expect(envelope).toBe(input.operationId);
  const labelId =
    input.kind === "WORK"
      ? "act.UPDATE_WORK.label"
      : (`act.REMINDER_${input.act.kind}.label` as const);
  expect(label).toBe(ui(language, labelId));
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const current = page.locator(".case-detail");
  await expectCaseReadsSettled(page);
  // A workspace retry revalidates identity and may discard transient success text.
  // The accepted response and fresh authorized version establish completion independently.
  const { target } = receipt.context;
  if (target.kind === "WORK") {
    const fresh = await workRequest(page, "case.get", {
      target: { kind: "ID", caseId: target.caseId },
    });
    if (fresh.outcome.kind !== "SUCCEEDED") {
      throw new Error("Authorized work readback required");
    }
    expect(fresh.outcome.data.work.revision).toBe(target.revision);
  } else if (target.kind === "REMINDER") {
    for (const version of target.versions) {
      const fresh = await workRequest(page, "reminder.get", {
        target: { kind: "ID", caseId: target.caseId },
        reminderId: version.reminderId,
      });
      if (fresh.outcome.kind !== "SUCCEEDED") {
        throw new Error("Authorized reminder readback required");
      }
      expect(fresh.outcome.data.item.reminder.revision).toBe(version.revision);
    }
  }
  await expect(current.locator(".entries-view [data-entry='responsible']")).toBeVisible();
  await expect(current.locator(".entries-view [data-entry='handlingSummary']")).toBeVisible();
  await expect(current.locator(".case-reminders")).toBeVisible();
  await expectCaseReadsSettled(page);
  return { operationId: envelope, document: receipt };
};
