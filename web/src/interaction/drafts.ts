import type { RequestByEndpoint } from "@contracts/client";
export type CommandKind = RequestByEndpoint["case.execute"]["act"]["kind"];
export type CorrectionGroupName = "registration" | "decision" | "payment";
export type CorrectionMode = "KEEP" | "REPLACE" | "CLEAR";
export type FlatDraftValues = Record<string, string>;
type CorrectionGroupValues = { mode: CorrectionMode; values: FlatDraftValues };
export type GroupedDraftValues = Record<string, CorrectionGroupValues>;
export type DraftValues = FlatDraftValues | GroupedDraftValues;
const isFlatValues = (value: unknown): value is FlatDraftValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.values(value).every((entry) => typeof entry === "string");
const isGroupValues = (value: unknown): value is CorrectionGroupValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  "mode" in value &&
  (value.mode === "KEEP" || value.mode === "REPLACE" || value.mode === "CLEAR") &&
  "values" in value &&
  isFlatValues(value.values);
export const isGroupedDraftValues = (value: unknown): value is GroupedDraftValues =>
  typeof value === "object" &&
  value !== null &&
  !Array.isArray(value) &&
  Object.values(value).length > 0 &&
  Object.values(value).every(isGroupValues);
export const hasRequiredCorrectionGroups = (
  value: unknown,
  names: ReadonlyArray<string>,
): value is GroupedDraftValues =>
  isGroupedDraftValues(value) && names.every((name) => Object.hasOwn(value, name));
const registration = (values: FlatDraftValues) => ({
  incidentDate: values["incidentDate"] ?? "",
  incidentNotificationDate: values["incidentNotificationDate"] ?? "",
  incidentCountry: values["incidentCountry"] ?? "",
  claimantName: values["claimantName"] ?? "",
  insurerName: values["insurerName"] ?? "",
  claimedAmount: values["claimedAmount"] ?? "",
  claimedCurrency: values["claimedCurrency"] ?? "",
});
const decision = (values: FlatDraftValues) => ({
  paymentDecisionDate: values["paymentDecisionDate"] ?? "",
  payableAmount: values["payableAmount"] ?? "",
  payableCurrency: values["payableCurrency"] ?? "",
});
const payment = (values: FlatDraftValues) => ({ paymentDate: values["paymentDate"] ?? "" });
type Act = RequestByEndpoint["case.execute"]["act"];
const correction = (values: DraftValues): Extract<Act, { kind: "CORRECT_CASE" }> => {
  if (!hasRequiredCorrectionGroups(values, ["registration", "decision", "payment"])) {
    throw new Error("CORRECT_CASE requires grouped values.");
  }
  const r = values["registration"]!;
  const d = values["decision"]!;
  const p = values["payment"]!;
  if (r.mode === "CLEAR") {
    throw new Error("Unsupported registration correction choice.");
  }
  return {
    kind: "CORRECT_CASE",
    groups: {
      registration:
        r.mode === "REPLACE"
          ? { mode: "REPLACE", values: registration(r.values) }
          : { mode: "KEEP" },
      decision:
        d.mode === "REPLACE" ? { mode: "REPLACE", values: decision(d.values) } : { mode: d.mode },
      payment:
        p.mode === "REPLACE" ? { mode: "REPLACE", values: payment(p.values) } : { mode: p.mode },
    },
  };
};
const reason = (values: FlatDraftValues): string => values["reason"] ?? "";
const flatAct = (kind: Exclude<CommandKind, "CORRECT_CASE">, values: FlatDraftValues): Act => {
  switch (kind) {
    case "OPEN":
    case "AMEND_REGISTRATION":
      return { kind, values: registration(values) };
    case "DECIDE":
      return { kind, values: decision(values) };
    case "RECORD_PAYMENT":
      return { kind, values: payment(values) };
    case "VOID":
    case "REINSTATE":
      return { kind, reason: reason(values) };
    case "WITHDRAW_DECISION":
    case "CLEAR_PAYMENT":
    case "CLOSE":
    case "REOPEN":
    case "ERASE":
      return { kind };
    default:
      throw new Error("Unsupported case act.");
  }
};
export const createDraft = (
  operationId: string,
  caseReference: string,
  expectedRevision: number,
  command: CommandKind,
  values: DraftValues,
): RequestByEndpoint["case.execute"] => {
  if (command === "CORRECT_CASE") {
    return { operationId, caseReference, expectedRevision, act: correction(values) };
  }
  if (isGroupedDraftValues(values)) {
    throw new Error(`${command} requires flat values.`);
  }
  return { operationId, caseReference, expectedRevision, act: flatAct(command, values) };
};
