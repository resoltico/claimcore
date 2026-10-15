import { randomUUID } from "node:crypto";
import type { CaseFields, CommandDraft, CaseReceipt } from "../src/api/types";
import { expect, type Page } from "@playwright/test";
import { startCommand } from "./case-workflow";
import { browserRequest, sessionToken } from "./session-helpers";
import { isResponse } from "../src/api/responseGuards";
import { endpoints } from "@contracts/endpoint-catalog";
import { selectLanguage } from "./localization-support";
import type { Language } from "../src/presentation/preferences";

export type Modes = Readonly<{
  registration: "KEEP" | "REPLACE";
  decision: "KEEP" | "REPLACE" | "CLEAR";
  payment: "KEEP" | "REPLACE" | "CLEAR";
}>;
type SeedStep = Readonly<{
  act: CommandDraft["act"];
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
    ...setupRegistration,
    claimedAmount: "1200.5",
    caseReference: reference,
    paymentDecisionDate: null,
    payableAmount: null,
    payableCurrency: null,
    paymentDate: null,
    status: "OPENED",
  };
  const decided: CaseFields = { ...opened, ...setupDecision };
  const paid: CaseFields = { ...decided, paymentDate: "2026-09-05" };
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
  request: CommandDraft,
  fields: CaseFields,
): Promise<CaseReceipt> => {
  const endpoint = endpoints.find((item) => item.id === "case.execute");
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
  if (!isResponse("case.execute", reply.payload)) {
    throw new Error("E2E_CORRECTION_SETUP_RESPONSE_INVALID");
  }
  const { outcome } = reply.payload;
  if (outcome.tag !== "ACCEPTED") {
    throw new Error(`E2E_CORRECTION_SETUP_${outcome.tag}`);
  }
  const receipt = outcome.data;
  const revision = request.expectedRevision + 1;
  expect(receipt.operationId).toBe(request.operationId);
  expect(receipt.caseReference).toBe(request.caseReference);
  expect(receipt.act).toBe(request.act.kind);
  expect(receipt.revision).toBe(revision);
  expect(receipt.snapshot).toEqual({
    tag: "DISCLOSED",
    state: { tag: "LIVE", disposition: "ACTIVE", fields },
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
        operationId: randomUUID(),
        caseReference: reference,
        expectedRevision: revision,
        act: step.act,
      },
      step.fields,
    );
    const { recordedBy: acceptedActor, revision: acceptedRevision } = receipt;
    if (recordedBy !== undefined) {
      expect(acceptedActor).toBe(recordedBy);
    }
    recordedBy = acceptedActor;
    revision = acceptedRevision;
  }
  await page.getByLabel("Handler's case reference (exact)").fill(reference);
  await page.getByRole("button", { name: "Find case", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
  await expect(
    page.locator("section.case-fields").first().getByText(`Revision ${revision}`),
  ).toBeVisible();
};
export const chooseGroups = async (page: Page, modes: Modes, language: Language) => {
  await startCommand(page, "Correct case facts");
  await selectLanguage(page, language);
  for (const [group, mode] of Object.entries(modes)) {
    await page.locator(`#correction-${group}-mode`).selectOption(mode);
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
        await page.locator(`input[name="${group}.${name}"]`).fill(value);
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
  if (payload.outcome.tag !== "SUCCEEDED" || payload.outcome.data.state.tag !== "LIVE") {
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
  if (payload.outcome.tag !== "SUCCEEDED") {
    throw new Error("E2E_CORRECTION_HISTORY_MISSING");
  }
  return payload.outcome.data.entries;
};
export const expectResultFields = (
  before: Awaited<ReturnType<typeof currentCase>>,
  after: Awaited<ReturnType<typeof currentCase>>,
  modes: Modes,
) => {
  expect(after).toEqual({
    revision: before.revision + 1,
    fields: {
      ...before.fields,
      ...(modes.registration === "REPLACE"
        ? { claimantName: "A\u0308 العربية\u200D <tag>", claimedAmount: "1" }
        : {}),
      ...(modes.decision === "REPLACE"
        ? { paymentDecisionDate: "2026-09-04", payableAmount: "400.25", payableCurrency: "USD" }
        : {}),
      ...(modes.decision === "CLEAR"
        ? { paymentDecisionDate: null, payableAmount: null, payableCurrency: null }
        : {}),
      ...(modes.payment === "REPLACE" ? { paymentDate: "2026-09-06" } : {}),
      ...(modes.payment === "CLEAR" ? { paymentDate: null } : {}),
    },
  });
};
