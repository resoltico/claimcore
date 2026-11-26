import type { Notice } from "../protocol/notices";
import { diagnosticMessageKeys } from "@contracts/messages";
import { exactInteger, recordedTime } from "./format";
import { renderKey, translate, renderCatalogKey } from "./messages";
import type { MessageKey } from "./types";
import type { Preferences } from "./preferences";

const localMessageKey = (reason: Extract<Notice, { kind: "local" }>["reason"]): MessageKey => {
  switch (reason) {
    case "downloadFailed":
      return "ui.downloadFailed";
    case "resourceUnavailable":
      return "ui.resourceUnavailable";
    case "recoveryCleanupUnavailable":
      return "ui.recoveryCleanupUnavailable";
    case "recoveryCoordinationUnavailable":
      return "ui.recoveryCoordinationUnavailable";
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
      return notice.screen.regions
        .flatMap((region) =>
          region.content.kind === "NOTICE"
            ? region.content.notices.flatMap((item) => [
                renderCatalogKey(preferences, item.messageId),
                ...(item.reason?.kind === "INSTANT"
                  ? [recordedTime(item.reason.value, "UTC", preferences.displayLocale)]
                  : []),
              ])
            : [],
        )
        .join(" ");
    case "accepted":
      return translate(preferences, "notice.accepted", { operationId: notice.operationId });
  }
};
