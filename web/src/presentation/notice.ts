import type { Notice } from "../protocol/notices";
import { diagnosticMessageKeys } from "@contracts/messages";
import { exactInteger } from "./format";
import { renderKey, translate } from "./messages";
import type { MessageKey } from "./types";
import type { Preferences } from "./preferences";

const localMessageKey = (reason: Extract<Notice, { kind: "local" }>["reason"]): MessageKey => {
  switch (reason) {
    case "resourceUnavailable":
      return "ui.resourceUnavailable";
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
  notice: Exclude<Notice, { kind: "human" | "humanIssues" }>,
): string => {
  switch (notice.kind) {
    case "local": {
      const key = localMessageKey(notice.reason);
      return renderKey(preferences, key);
    }
    case "diagnostic":
      return renderKey(preferences, diagnosticMessageKeys[notice.diagnostic.code]);
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
