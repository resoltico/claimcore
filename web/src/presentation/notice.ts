import type { Notice } from "../api/notices";
import requirements from "./generated/diagnostic-parameters.json";
import { exactInteger } from "./format";
import { renderKey, translate } from "./messages";
import type { Preferences } from "./preferences";

const limits: Readonly<
  Record<string, Readonly<Record<string, { minimum?: number; maximum?: number }>>>
> = requirements;
const diagnosticText = (
  preferences: Preferences,
  diagnostic: Extract<Notice, { kind: "diagnostic" }>["diagnostic"],
): string => {
  const expected = Object.hasOwn(limits, diagnostic.id) ? limits[diagnostic.id] : undefined;
  const supplied: Readonly<Record<string, number>> = diagnostic.parameters;
  if (typeof supplied !== "object" || supplied === null || Array.isArray(supplied))
    return translate(preferences, "notice.unknownDiagnostic");
  const keys = Object.keys(supplied).sort().join(",");
  if (expected === undefined || keys !== Object.keys(expected).sort().join(","))
    return translate(preferences, "notice.unknownDiagnostic");
  const valid = Object.entries(supplied).every(
    ([key, value]) =>
      Number.isSafeInteger(value) &&
      value >= (expected[key]?.minimum ?? 0) &&
      value <= (expected[key]?.maximum ?? 2147483647),
  );
  if (!valid) return translate(preferences, "notice.unknownDiagnostic");
  const parameters = Object.fromEntries(
    Object.entries(supplied).map(([k, v]) => [
      k,
      exactInteger(String(v), preferences.displayLocale),
    ]),
  );
  return renderKey(preferences, `diagnostic.${diagnostic.id}`, parameters);
};
export const renderNotice = (preferences: Preferences, notice: Notice): string => {
  switch (notice.kind) {
    case "local":
      return renderKey(preferences, `notice.${notice.reason}`);
    case "diagnostic":
      return diagnosticText(preferences, notice.diagnostic);
    case "invalidHttp":
      return translate(preferences, "notice.invalidHttp", {
        status: exactInteger(String(notice.status), preferences.displayLocale),
      });
    case "fileTooLarge":
      return translate(preferences, "notice.fileTooLarge", {
        maximumBytes: exactInteger(String(notice.maximumBytes), preferences.displayLocale),
      });
    case "accepted":
      return translate(preferences, "notice.accepted", { operationId: notice.operationId });
    case "recovery":
      return `${renderNotice(preferences, notice.cause)} ${renderKey(preferences, `notice.${notice.direction}`)}`;
  }
};
