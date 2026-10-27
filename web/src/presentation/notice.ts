import type { Notice } from "../protocol/notices";
import { diagnosticParameters as requirements } from "@contracts/messages";
import { exactInteger } from "./format";
import { renderKey, translate } from "./messages";
import type { Preferences } from "./preferences";

const limits: Readonly<
  Record<
    string,
    Readonly<Record<string, { minimum?: number; maximum?: number; enum?: ReadonlyArray<string> }>>
  >
> = requirements;
const contextFields = new Set(["code", "action", "target", "actualRevision", "phase"]);
const diagnosticText = (
  preferences: Preferences,
  diagnostic: Extract<Notice, { kind: "diagnostic" }>["diagnostic"],
): string => {
  const keys = ["diagnostic", "fault", "host"]
    .map((family) => `${family}.${diagnostic.code}`)
    .filter((key) => Object.hasOwn(limits, key));
  const messageKey = keys.length === 1 ? keys[0] : undefined;
  const expected = messageKey === undefined ? undefined : limits[messageKey];
  const supplied: Readonly<Record<string, unknown>> = Object.fromEntries(
    Object.entries(diagnostic).filter(([key]) => !contextFields.has(key)),
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
    Object.entries(supplied).map(([k, v]) => [
      k,
      typeof v === "string" ? v : exactInteger(String(v), preferences.displayLocale),
    ]),
  );
  return renderKey(preferences, messageKey, parameters);
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
    case "accepted":
      return translate(preferences, "notice.accepted", { operationId: notice.operationId });
  }
};
