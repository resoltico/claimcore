import { capabilityRoleRequirements } from "@contracts/client";
import { isTerm, termLabel } from "./terms";
import type { Notice } from "../protocol/notices";
import {
  diagnosticParameters as requirements,
  diagnosticMessageKeys,
  diagnosticGuiMessageKeys,
} from "@contracts/messages";
import { calendarDate, exactInteger } from "./format";
import { renderCatalogKey, renderKey, translate } from "./messages";
import type { MessageKey } from "./types";
import type { Preferences } from "./preferences";

const limits: Readonly<
  Record<
    string,
    Readonly<Record<string, { minimum?: number; maximum?: number; enum?: ReadonlyArray<string> }>>
  >
> = requirements;
const isDiagnosticCode = (code: string): code is keyof typeof diagnosticMessageKeys =>
  Object.hasOwn(diagnosticMessageKeys, code);
const contextFields = new Set([
  "code",
  "action",
  "target",
  "actualRevision",
  "phase",
  "field",
  "issues",
  "relatedDate",
]);
const requiredRoleNames = (preferences: Preferences, capability: string): string => {
  const rolePolicies: Readonly<Record<string, ReadonlyArray<string>>> = capabilityRoleRequirements;
  const roles = rolePolicies[capability];
  if (roles === undefined || roles.length === 0) {
    return translate(preferences, "notice.unknownDiagnostic");
  }
  const labels = roles.map((role) =>
    isTerm("role", role)
      ? termLabel(preferences, "role", role)
      : translate(preferences, "notice.unknownDiagnostic"),
  );
  return labels.reduce((first, second) =>
    renderKey(preferences, "notice.alternativeRoles", { first, second }),
  );
};
const diagnosticEnum = (
  preferences: Preferences,
  code: string,
  key: string,
  value: string,
): string => {
  if (code === "NOT_AUTHORIZED" && key === "required") {
    return requiredRoleNames(preferences, value);
  }
  if (code === "NOT_OFFERED" && key === "state" && isTerm("headline", value)) {
    return termLabel(preferences, "headline", value);
  }
  if (code === "REMINDER_NOT_OFFERED" && key === "state" && isTerm("reminderState", value)) {
    return termLabel(preferences, "reminderState", value);
  }
  return value;
};
const diagnosticText = (
  preferences: Preferences,
  diagnostic: Extract<Notice, { kind: "diagnostic" }>["diagnostic"],
): string => {
  const messageKey = isDiagnosticCode(diagnostic.code)
    ? diagnosticMessageKeys[diagnostic.code]
    : undefined;
  const expected = messageKey === undefined ? undefined : limits[messageKey];
  const supplied: Readonly<Record<string, unknown>> =
    diagnostic.code === "INVALID_INPUT" && "issues" in diagnostic
      ? { issueCount: diagnostic.issues.length }
      : Object.fromEntries(
          Object.entries(diagnostic).filter(
            ([key]) =>
              !contextFields.has(key) &&
              !(diagnostic.code === "REVISION_CONFLICT" && key === "changes"),
          ),
        );
  const suppliedKeys = Object.keys(supplied).sort().join(",");
  if (
    messageKey === undefined ||
    expected === undefined ||
    suppliedKeys !== Object.keys(expected).sort().join(",")
  ) {
    return translate(preferences, "notice.unknownDiagnostic");
  }
  const valid = Object.entries(supplied).every(([key, value]) =>
    typeof value === "string"
      ? expected[key]?.enum?.includes(value) === true
      : typeof value === "number" &&
        Number.isSafeInteger(value) &&
        value >= (expected[key]?.minimum ?? 0) &&
        value <= (expected[key]?.maximum ?? 2147483647),
  );
  if (!valid) {
    return translate(preferences, "notice.unknownDiagnostic");
  }
  const parameters = Object.fromEntries(
    Object.entries(supplied).map(([key, value]) => {
      if (typeof value !== "string") {
        return [key, exactInteger(String(value), preferences.displayLocale)];
      }
      const displayed = diagnosticEnum(preferences, diagnostic.code, key, value);
      return [key, displayed];
    }),
  );
  const displayedKey = diagnosticGuiMessageKeys[diagnostic.code];
  const message = renderKey(preferences, displayedKey, parameters);
  if ("relatedDate" in diagnostic && diagnostic.relatedDate !== undefined) {
    const related = diagnostic.relatedDate;
    return `${message} (${renderCatalogKey(preferences, `field.${related.field}.label`)}: ${calendarDate(related.value, preferences.displayLocale)})`;
  }
  return message;
};
const localMessageKey = (reason: Extract<Notice, { kind: "local" }>["reason"]): MessageKey => {
  switch (reason) {
    case "recoveryCleanupUnavailable":
      return "ui.recoveryCleanupUnavailable";
    case "recoveryListingUnavailable":
      return "ui.recoveryListingUnavailable";
    case "recoveryUnavailable":
      return "ui.recoveryUnavailable";
    case "unresolved":
      return "ui.operationUnresolved";
    case "incomplete":
      return "ui.incompleteResponse";
    default:
      return `notice.${reason}`;
  }
};
export const renderNotice = (
  preferences: Preferences,
  notice: Exclude<Notice, { kind: "human" }>,
): string => {
  switch (notice.kind) {
    case "issues":
      return notice.issues.map((issue) => diagnosticText(preferences, issue)).join(" ");
    case "local": {
      const key = localMessageKey(notice.reason);
      return renderKey(preferences, key);
    }
    case "diagnostic":
      return diagnosticText(preferences, notice.diagnostic);
    case "invalidHttp":
      return translate(preferences, "notice.invalidHttp", {
        status: exactInteger(String(notice.status), preferences.displayLocale),
      });
    case "pending":
      return translate(preferences, "ui.unconfirmedUntil", { finalAt: notice.finalAt });
    case "accepted":
      return translate(preferences, "notice.accepted", { operationId: notice.operationId });
  }
};
