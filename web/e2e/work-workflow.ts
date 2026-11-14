import { type ResponseFor, endpoints, type RequestByEndpoint } from "@contracts/client";
import { chooseOption } from "./workspace-navigation";
import { ui } from "./localization-support";
import type { ReviewIdentity } from "./case-workflow";
import { expect, type Page, type Locator, type Request } from "@playwright/test";
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
      name: ui(await languageOf(page), "task.reminder.complete.label"),
      exact: false,
    });
    const originId = await circle.getAttribute("id");
    await circle.click();
    const delivered = await response;
    const input: unknown = delivered.request().postDataJSON();
    const payload: unknown = await delivered.json();
    const operationPreviewRequest = input as RequestByEndpoint["operation.preview"];
    const operationPreviewReply = payload as ResponseFor<"operation.preview", "HUMAN">;
    if (
      delivered.status() !== 200 ||
      operationPreviewRequest.binding.kind !== "REMINDER" ||
      operationPreviewRequest.task !== "reminder.complete" ||
      operationPreviewReply.representation !== "HUMAN" ||
      operationPreviewReply.outcome.kind !== "REVIEWED"
    ) {
      throw new Error("E2E_REMINDER_COMPLETION_REVIEW_UNAVAILABLE");
    }
    expect(operationPreviewReply.operationId).toBe(operationPreviewRequest.operationId);
    expect(originId).toBe(
      `reminder-${operationPreviewRequest.binding.reminderId}-reminder.complete`,
    );
    await expect(page.locator(`[id="${originId}"]`)).toHaveCount(1);
    await expect(workConfirmButton(page)).toHaveCount(1);
    await expect(workConfirmButton(page)).toBeVisible();
    await expect(workConfirmButton(page)).toBeEnabled();
    await expect(workConfirmButton(page)).toHaveText(
      ui(await languageOf(page), "task.reminder.complete.label"),
    );
    expect(calls).toEqual(["/api/operation.preview"]);
    return {
      operationId: operationPreviewRequest.operationId,
    };
  } finally {
    page.off("request", observed);
  }
};

/** Select the supplied row control; review and confirmation remain separate steps. */
export const selectReminderAction = async (
  row: Locator,
  kind: "reminder.update" | "reminder.cancel" | "reminder.reopen" | "reminder.completeAndPlanNext",
) => {
  await row.locator(`button[data-act="${kind}"]`).click();
};

/** Reuse the authenticated action's observed binding for its independent DATA readback. */
export const observedAccess = (request: Request) => {
  const headers = request.headers();
  const installation = headers["claimcore-installation"];
  const token = headers["x-claimcore-antiforgery"];
  if (installation === undefined || token === undefined) {
    throw new Error("Accepted action is missing its authenticated installation binding.");
  }
  return { installationId: installation, token };
};

const acceptedWorkSubmission = (family: "WORK" | "REMINDER", input: unknown, payload: unknown) => {
  const operationExecuteRequest = input as RequestByEndpoint["operation.execute"];
  if (
    (operationExecuteRequest.binding.kind !== "WORK" &&
      operationExecuteRequest.binding.kind !== "REMINDER") ||
    operationExecuteRequest.binding.kind !== family
  ) {
    throw new Error("E2E_WORK_SUBMIT_INPUT_MISMATCH");
  }
  const operationExecuteReply = payload as ResponseFor<"operation.execute", "HUMAN">;
  if (
    operationExecuteReply.representation !== "HUMAN" ||
    (operationExecuteReply.outcome.kind !== "ACCEPTED" &&
      operationExecuteReply.outcome.kind !== "ALREADY_ACCEPTED") ||
    operationExecuteReply.outcome.document.subject.operationId !==
      operationExecuteRequest.operationId
  ) {
    throw new Error("E2E_WORK_SUBMIT_FAILED");
  }
  return {
    input: operationExecuteRequest,
    envelope: operationExecuteReply.operationId,
    receipt: operationExecuteReply.outcome.document,
  };
};
export const workRequest = async <K extends keyof RequestByEndpoint>(
  page: Page,
  endpoint: K,
  body: RequestByEndpoint[K],
  access?: { installationId: string; token: string },
) => {
  const token = access?.token ?? (await sessionToken(page));
  const entry = endpoints.find((value) => value.id === endpoint)!;
  const result = await browserRequest(page, entry.path, {
    method: entry.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
      ...(entry.requiresInstallation
        ? {
            "ClaimCore-Installation": access?.installationId ?? (await installationId(page, token)),
          }
        : {}),
    },
    body: JSON.stringify(body),
  });
  const endpointReply = result.payload as ResponseFor<typeof endpoint, "DATA">;
  if (
    result.status !== 200 ||
    !(!("representation" in endpointReply) || endpointReply.representation === "DATA")
  ) {
    const detail = isHostFailure(endpointReply, result.status)
      ? endpointReply.hostFailure.code
      : "INVALID_RESPONSE";
    throw new Error(`E2E_WORK_READBACK_HTTP_${result.status}_${detail}`);
  }
  return endpointReply;
};
/** Check durable facts through DATA independently of the HUMAN receipt display. */
export const acceptedSubject = async (
  page: Page,
  operationId: string,
  access?: { installationId: string; token: string },
) => {
  const reply = await workRequest(page, "operation.get", { operationId }, access);
  if (reply.outcome.kind !== "ACCEPTED") {
    throw new Error("Expected accepted durable operation.");
  }
  return reply.outcome.data.receipt.subject;
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
      ? (["task.work.update.label"] as const)
      : ([
          "task.reminder.create.label",
          "task.reminder.update.label",
          "task.reminder.complete.label",
          "task.reminder.cancel.label",
          "task.reminder.reopen.label",
          "task.reminder.completeAndPlanNext.label",
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
    input.binding.kind === "WORK"
      ? "task.work.update.label"
      : (`task.${input.task}.label` as const);
  expect(label).toBe(ui(language, labelId));
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const current = page.locator(".case-detail");
  await expectCaseReadsSettled(page);
  // A workspace retry revalidates identity and may discard transient success text.
  // The accepted response and fresh authorized version establish completion independently.
  const access = observedAccess(delivered.request());
  const target = await acceptedSubject(page, receipt.subject.operationId, access);
  if (target.kind === "WORK") {
    const fresh = await workRequest(page, "case.get", {
      target: {
        kind: "ID",
        caseId: target.caseId,
      },
    });
    if (fresh.outcome.kind !== "SUCCEEDED") {
      throw new Error("Authorized work readback required");
    }
    expect(fresh.outcome.data.work.revision).toBe(target.revision);
  } else if (target.kind === "REMINDER") {
    const versions =
      target.effects.kind === "SINGLE"
        ? [target.effects.reminder]
        : [target.effects.completed, target.effects.next];
    for (const version of versions) {
      const fresh = await workRequest(page, "reminder.get", {
        target: {
          kind: "ID",
          caseId: target.caseId,
        },
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
  return {
    operationId: envelope,
    document: receipt,
  };
};
