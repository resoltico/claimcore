import { showExactLookup, chooseChoice } from "./workspace-navigation";
import { fillControl } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import type { CaseFields, OperationDraft, CaseReceipt } from "../src/protocol/types";
import { expect, type Page } from "@playwright/test";
import { startAct } from "./case-workflow";
import { browserRequest, sessionToken } from "./session-helpers";
import { isResponse } from "../src/protocol/client";
import { endpoints } from "@contracts/client";
import { selectLanguage } from "./localization-support";
import type { Language } from "../src/presentation/preferences";

export type Modes = Readonly<{
  registration: "KEEP" | "REPLACE";
  decision: "KEEP" | "REPLACE" | "CLEAR";
  payment: "KEEP" | "REPLACE" | "CLEAR";
}>;
type SeedStep = Readonly<{
  act: OperationDraft["act"];
  fields: CaseFields;
}>;

const setupRegistration = registration({ claimedAmount: "1200.50" });

const setupDecision = {
  paymentDecisionDate: "2026-09-03",
  payableAmount: "300.25",
  payableCurrency: "EUR",
};

const paidCaseSteps = (reference: string, status: "OPENED" | "CLOSED"): SeedStep[] => {
  const opened: CaseFields = {
    caseReference: reference,
    status: "OPENED",
    registration: { ...setupRegistration, claimedAmount: "1200.5" },
    decision: null,
    payment: null,
  };
  const decided: CaseFields = { ...opened, decision: setupDecision };
  const paid: CaseFields = { ...decided, payment: { paymentDate: "2026-09-05" } };
  const steps: SeedStep[] = [
    { act: { kind: "OPEN", values: setupRegistration }, fields: opened },
    { act: { kind: "DECIDE", values: setupDecision }, fields: decided },
    {
      act: {
        kind: "RECORD_PAYMENT",
        values: { paymentDate: "2026-09-05" },
      },
      fields: paid,
    },
  ];
  if (status === "CLOSED") {
    steps.push({
      act: { kind: "CLOSE" },
      fields: { ...paid, status: "CLOSED" },
    });
  }
  return steps;
};

const acceptSetup = async (
  page: Page,
  token: string,
  request: OperationDraft,
  fields: CaseFields,
): Promise<CaseReceipt> => {
  const endpoint = endpoints.find((item) => item.id === "operation.execute");
  if (endpoint === undefined) {
    throw new Error("E2E_CORRECTION_SETUP_ENDPOINT_MISSING");
  }
  const reply = await browserRequest(page, endpoint.path, {
    method: endpoint.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
    },
    body: JSON.stringify(request),
  });
  expect(reply.status).toBe(200);
  expect(reply.cacheControl).toContain("no-store");
  if (!isResponse("operation.execute", reply.payload)) {
    throw new Error("E2E_CORRECTION_SETUP_RESPONSE_INVALID");
  }
  const { outcome } = reply.payload;
  if (outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_CORRECTION_SETUP_${outcome.kind}`);
  }
  if (outcome.data.subject.kind !== "CASE") {
    throw new Error("Expected case receipt.");
  }
  const receipt: CaseReceipt = { ...outcome.data, subject: outcome.data.subject };
  const revision = request.expectedRevision + 1;
  expect(receipt.operationId).toBe(request.operationId);
  expect(receipt.subject.caseReference).toBe(request.caseReference);
  expect(receipt.subject.act).toBe(request.act.kind);
  expect(receipt.subject.revision).toBe(revision);
  expect(receipt.subject.snapshot).toEqual({
    kind: "DISCLOSED",
    view: { kind: "ACTIVE", fields },
  });
  return receipt;
};

export const paidCase = async (page: Page, reference: string, status: "OPENED" | "CLOSED") => {
  const token = await sessionToken(page);
  let recordedBy: string | undefined;
  let revision = 0;
  for (const step of paidCaseSteps(reference, status)) {
    const receipt = await acceptSetup(
      page,
      token,
      {
        kind: "CASE",
        operationId: randomUUID(),
        caseReference: reference,
        expectedRevision: revision,
        act: step.act,
      },
      step.fields,
    );
    const { recordedBy: acceptedActor } = receipt;
    const acceptedRevision = receipt.subject.revision;
    if (recordedBy !== undefined) {
      expect(acceptedActor).toBe(recordedBy);
    }
    recordedBy = acceptedActor;
    revision = acceptedRevision;
  }
  await (await showExactLookup(page)).fill(reference);
  await page.getByRole("button", { name: "Open by exact reference", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
  await expect(page.locator(".case-header-state").getByText(`Revision ${revision}`)).toBeVisible();
};
export const chooseGroups = async (page: Page, modes: Modes, language: Language) => {
  await startAct(page, "Correct case facts");
  await selectLanguage(page, language);
  for (const [group, mode] of Object.entries(modes)) {
    await chooseChoice(page, `act.groups.${group}.kind`, mode);
  }
  const replacements = {
    registration: { claimantName: "A\u0308 العربية\u200D <tag>", claimedAmount: "1.0000" },
    decision: {
      paymentDecisionDate: "2026-09-04",
      payableAmount: "400.2500",
      payableCurrency: "USD",
    },
    payment: { paymentDate: "2026-09-06" },
  };
  for (const group of ["registration", "decision", "payment"] as const) {
    if (modes[group] === "REPLACE") {
      for (const [name, value] of Object.entries(replacements[group])) {
        await fillControl(page.locator(`[data-field-name="${name}"]:not(.field-row)`), value);
      }
    }
  }
};
export const currentCase = async (page: Page, caseReference: string) => {
  const result = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference }),
  });
  if (!isResponse("case.get", result.payload)) {
    throw new Error("E2E_CORRECTION_CASE_READBACK_INVALID");
  }
  const { payload } = result;
  if (payload.outcome.kind !== "SUCCEEDED" || payload.outcome.data.state.kind === "ERASED") {
    throw new Error("E2E_CORRECTION_CASE_MISSING");
  }
  return { revision: payload.outcome.data.revision, fields: payload.outcome.data.state.fields };
};
export const history = async (page: Page, caseReference: string) => {
  const result = await browserRequest(page, "/api/case.history", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference, limit: 50, detail: "FULL" }),
  });
  if (!isResponse("case.history", result.payload)) {
    throw new Error("E2E_CORRECTION_HISTORY_READBACK_INVALID");
  }
  const { payload } = result;
  if (payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("E2E_CORRECTION_HISTORY_MISSING");
  }
  return payload.outcome.data.entries;
};
export const expectResultFields = (
  before: Awaited<ReturnType<typeof currentCase>>,
  after: Awaited<ReturnType<typeof currentCase>>,
  modes: Modes,
) => {
  const decisions = {
    KEEP: before.fields.decision,
    REPLACE: { paymentDecisionDate: "2026-09-04", payableAmount: "400.25", payableCurrency: "USD" },
    CLEAR: null,
  };
  const payments = {
    KEEP: before.fields.payment,
    REPLACE: { paymentDate: "2026-09-06" },
    CLEAR: null,
  };
  expect(after).toEqual({
    revision: before.revision + 1,
    fields: {
      ...before.fields,
      registration:
        modes.registration === "REPLACE"
          ? {
              ...before.fields.registration,
              claimantName: "A\u0308 العربية\u200D <tag>",
              claimedAmount: "1",
            }
          : before.fields.registration,
      decision: decisions[modes.decision],
      payment: payments[modes.payment],
    },
  });
};
