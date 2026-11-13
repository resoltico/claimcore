import { operationId as mintOperationId } from "../src/operation/operationId";
import { showCaseRecordDetails, showExactLookup, chooseChoice } from "./workspace-navigation";
import { fillControl } from "./typed-entry";
import type { CaseFields, OperationDraft } from "../src/protocol/types";
import { expect, type Page } from "@playwright/test";
import { startAct } from "./case-workflow";
import { browserRequest, sessionToken, installationId } from "./session-helpers";
import { isResponseFor } from "../src/protocol/client";
import { endpoints, type OperationReceipt } from "@contracts/client";
import { selectLanguage } from "./localization-support";

export type Modes = Readonly<{
  registration: "KEEP" | "UPDATE";
  decision: "KEEP" | "UPDATE" | "CLEAR";
  payable?: string;
}>;
type CaseDataReceipt = OperationReceipt & {
  subject: Extract<OperationReceipt["subject"], { kind: "CASE" }>;
};
type SeedStep = Readonly<{
  operationId?: string;
  act: OperationDraft["act"];
  fields: CaseFields;
}>;

const setupRegistration = {
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "LV",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "1200.50",
  claimedCurrency: "EUR",
};

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
    finance: {
      decision: null,
      paidAmount: "0",
      paymentCount: 0,
      firstPaymentDate: null,
      lastPaymentDate: null,
    },
  };
  const decided: CaseFields = {
    ...opened,
    finance: { ...opened.finance, decision: setupDecision },
  };
  const paymentId = mintOperationId();
  const paid: CaseFields = {
    ...decided,
    finance: {
      ...decided.finance,
      paidAmount: "300.25",
      paymentCount: 1,
      firstPaymentDate: "2026-09-05",
      lastPaymentDate: "2026-09-05",
    },
  };
  const steps: SeedStep[] = [
    { act: { kind: "case.open", values: setupRegistration }, fields: opened },
    { act: { kind: "case.decide", values: setupDecision }, fields: decided },
    {
      operationId: paymentId,
      act: {
        kind: "case.recordPayment",
        values: { paymentDate: "2026-09-05", paidAmount: "300.25", paidCurrency: "EUR" },
      },
      fields: paid,
    },
  ];
  if (status === "CLOSED") {
    steps.push({
      act: { kind: "case.close" },
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
): Promise<CaseDataReceipt> => {
  const endpoint = endpoints.find((item) => item.id === "operation.execute");
  if (endpoint === undefined) {
    throw new Error("E2E_CORRECTION_SETUP_ENDPOINT_MISSING");
  }
  const reply = await browserRequest(page, endpoint.path, {
    method: endpoint.method,
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": token,
      "ClaimCore-Installation": await installationId(page, token),
    },
    body: JSON.stringify(request),
  });
  expect(reply.status).toBe(200);
  expect(reply.cacheControl).toContain("no-store");
  if (!isResponseFor("operation.execute", "DATA", reply.payload)) {
    throw new Error("E2E_CORRECTION_SETUP_RESPONSE_INVALID");
  }
  const { outcome } = reply.payload;
  if (outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_CORRECTION_SETUP_${outcome.kind}`);
  }
  if (outcome.data.subject.kind !== "CASE") {
    throw new Error("Expected case receipt.");
  }
  const receipt: CaseDataReceipt = {
    ...outcome.data,
    subject: outcome.data.subject,
  };
  const revision = request.expectedRevision + 1;
  expect(receipt.operationId).toBe(request.operationId);
  if (request.target.kind !== "REFERENCE") {
    throw new Error("Reference fixture positive control");
  }
  expect(receipt.subject.caseReference).toBe(request.target.caseReference);
  expect(receipt.subject.act).toBe(request.act.kind);
  expect(receipt.subject.revision).toBe(revision);
  const recordedFields = (() => {
    switch (request.act.kind) {
      case "case.open":
        return [
          "incidentDate",
          "incidentNotificationDate",
          "incidentCountry",
          "incidentDescription",
          "policyNumber",
          "insurerClaimReference",
          "claimantName",
          "insurerName",
          "claimedAmount",
          "claimedCurrency",
        ];
      case "case.decide":
        return ["paymentDecisionDate", "payableAmount", "payableCurrency"];
      case "case.recordPayment":
        return ["paymentDate", "paidAmount", "paidCurrency"];
      case "case.close":
        return [];
      default:
        throw new Error("Unexpected paid-case setup act");
    }
  })();
  expect(receipt.subject.snapshot).toEqual({
    kind: "DISCLOSED",
    recordedFields,
    state: { kind: "ACTIVE", fields },
    payment:
      request.act.kind === "case.recordPayment"
        ? {
            kind: "RECORDED",
            entry: {
              paymentId: request.operationId,
              paymentDate: "2026-09-05",
              paidAmount: "300.25",
            },
          }
        : null,
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
        operationId: step.operationId ?? mintOperationId(),
        target: { kind: "REFERENCE", caseReference: reference },
        expectedRevision: revision,
        act: step.act,
      },
      step.fields,
    );
    const { recordedBy: acceptedActor } = receipt;
    const acceptedRevision = receipt.subject.revision;
    if (recordedBy !== undefined) {
      expect(acceptedActor.actorId).toBe(recordedBy);
    }
    recordedBy = acceptedActor.actorId;
    revision = acceptedRevision;
  }
  await (await showExactLookup(page)).fill(reference);
  await page.getByRole("button", { name: "Open by exact reference", exact: true }).click();
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await showCaseRecordDetails(page);
  await expect(
    page.locator(
      "section.case-detail > .screen-view > [data-region=DETAILS] .entries-view [data-entry='revision'] dd",
    ),
  ).toContainText(String(revision));
};
export const chooseGroups = async (page: Page, modes: Modes, language: "en" | "lv" | "ar") => {
  await selectLanguage(page, language);
  await startAct(page, "case.correct", language);
  for (const group of ["registration", "decision"] as const) {
    await chooseChoice(page, `group:${group}`, modes[group]);
  }
  const replacements = {
    registration: { claimantName: "A\u0308 العربية\u200D <tag>", claimedAmount: "1.0000" },
    decision: {
      paymentDecisionDate: "2026-09-04",
      payableAmount: modes.payable ?? "400.2500",
      payableCurrency: "EUR",
    },
  };
  for (const group of ["registration", "decision"] as const) {
    if (modes[group] === "UPDATE") {
      for (const name of Object.keys(replacements[group])) {
        const selected = page.locator(`input[data-selects-fields~="${name}"]`);
        await selected.check();
      }
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
    body: JSON.stringify({ target: { kind: "REFERENCE", caseReference } }),
  });
  if (!isResponseFor("case.get", "DATA", result.payload)) {
    throw new Error("E2E_CORRECTION_CASE_READBACK_INVALID");
  }
  const { payload } = result;
  if (payload.outcome.kind !== "SUCCEEDED" || payload.outcome.data.state.kind === "ERASED") {
    throw new Error("E2E_CORRECTION_CASE_MISSING");
  }
  return {
    caseId: payload.outcome.data.caseId,
    revision: payload.outcome.data.revision,
    fields: payload.outcome.data.state.fields,
  };
};
export const history = async (page: Page, caseReference: string) => {
  const result = await browserRequest(page, "/api/case.timeline", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      target: { kind: "REFERENCE", caseReference },
      limit: 50,
      filter: "FACTS",
    }),
  });
  if (!isResponseFor("case.timeline", "DATA", result.payload)) {
    throw new Error("E2E_CORRECTION_HISTORY_READBACK_INVALID");
  }
  const { payload } = result;
  if (payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("E2E_CORRECTION_HISTORY_MISSING");
  }
  return payload.outcome.data.items;
};
export const expectResultFields = (
  before: Awaited<ReturnType<typeof currentCase>>,
  after: Awaited<ReturnType<typeof currentCase>>,
  modes: Modes,
) => {
  const decisions = {
    KEEP: before.fields.finance.decision,
    UPDATE: { paymentDecisionDate: "2026-09-04", payableAmount: "400.25", payableCurrency: "EUR" },
    CLEAR: null,
  };
  expect(after).toEqual({
    caseId: before.caseId,
    revision: before.revision + 1,
    fields: {
      ...before.fields,
      registration:
        modes.registration === "UPDATE"
          ? {
              ...before.fields.registration,
              claimantName: "A\u0308 العربية\u200D <tag>",
              claimedAmount: "1",
            }
          : before.fields.registration,
      finance: { ...before.fields.finance, decision: decisions[modes.decision] },
    },
  });
};
