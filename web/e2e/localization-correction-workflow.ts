import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import { expect, type Page } from "@playwright/test";
import { completeCommand, openCase, startCommand } from "./case-workflow";
import { browserRequest, sessionToken } from "./session-helpers";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import { selectLanguage } from "./localization-support";
import type { Language } from "../src/presentation/preferences";

export type Modes = Readonly<{
  registration: "KEEP" | "REPLACE";
  decision: "KEEP" | "REPLACE" | "CLEAR";
  payment: "KEEP" | "REPLACE" | "CLEAR";
}>;
export const paidCase = async (page: Page, reference: string) => {
  await openCase(page, reference);
  await completeCommand(page, "Record payment decision", {
    "Payment decision date": "2026-09-03",
    "Amount to be paid": "300.25",
    "Currency of amount to be paid": "EUR",
  });
  await completeCommand(page, "Record actual payment", { "Payment date": "2026-09-05" });
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
  const result = await browserRequest(page, "/api/v3/cases/get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference }),
  });
  if (!(await isWebV3Response("case.get", result.payload))) {
    throw new Error("E2E_CORRECTION_CASE_READBACK_INVALID");
  }
  const payload = result.payload as WebV3Response<"case.get">;
  if (payload.outcome.tag !== "SUCCEEDED" || payload.outcome.data.tag !== "FOUND") {
    throw new Error("E2E_CORRECTION_CASE_MISSING");
  }
  return payload.outcome.data.current.case;
};
export const history = async (page: Page, caseReference: string) => {
  const result = await browserRequest(page, "/api/v3/cases/history", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference, limit: 50, detail: "FULL" }),
  });
  if (!(await isWebV3Response("case.history", result.payload))) {
    throw new Error("E2E_CORRECTION_HISTORY_READBACK_INVALID");
  }
  const payload = result.payload as WebV3Response<"case.history">;
  if (payload.outcome.tag !== "SUCCEEDED" || payload.outcome.data.tag !== "FOUND") {
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
    revision: "4",
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
