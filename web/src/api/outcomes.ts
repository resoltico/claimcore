import type { EndpointOutcome } from "../generated/contracts/web-v3.types";
import type { ApiResult } from "./types";
import { localNotice, type Notice } from "./notices";

type Download = { blob: Blob; filename: string };
const diagnosticFrom = (data: EndpointOutcome["outcome"]["data"]): Notice | null => {
  if (typeof data !== "object" || data === null) {
    return null;
  }
  if ("diagnostic" in data) {
    return { kind: "diagnostic", diagnostic: data.diagnostic };
  }
  if ("rejection" in data) {
    return { kind: "diagnostic", diagnostic: data.rejection.diagnostic };
  }
  if ("fault" in data) {
    return { kind: "diagnostic", diagnostic: data.fault.diagnostic };
  }
  if ("execution" in data) {
    const { execution } = data;
    if ("rejection" in execution) {
      return { kind: "diagnostic", diagnostic: execution.rejection.diagnostic };
    }
    if ("fault" in execution) {
      return { kind: "diagnostic", diagnostic: execution.fault.diagnostic };
    }
  }
  return null;
};
/** Preserve diagnostics as data. Server English is never parsed or used as a translation key. */
export const resultNotice = (result: ApiResult<EndpointOutcome | Download>): Notice => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return { kind: "diagnostic", diagnostic: result.failure.diagnostic };
  }
  const { value } = result;
  if (!("outcome" in value)) {
    return localNotice("incomplete");
  }
  const diagnostic = diagnosticFrom(value.outcome.data);
  if (diagnostic !== null) {
    return diagnostic;
  }
  switch (value.outcome.tag) {
    case "DISMISSED":
      return localNotice("dismissed");
    case "ALREADY_DISMISSED":
      return localNotice("alreadyDismissed");
    case "RETAINED":
      return localNotice("retained");
    case "EXISTING":
      return localNotice("existing");
    default:
      return localNotice("incomplete");
  }
};
const uncertainOutcome = (data: EndpointOutcome["outcome"]["data"]): boolean => {
  const recoverExact = (value: object): boolean =>
    "recommendedAction" in value && value.recommendedAction === "RECOVER_EXACT";
  if (
    data !== null &&
    typeof data === "object" &&
    (recoverExact(data) ||
      ("fault" in data && recoverExact(data.fault)) ||
      ("execution" in data && "fault" in data.execution && recoverExact(data.execution.fault)) ||
      ("settlement" in data && data.settlement === "UNCONFIRMED"))
  ) {
    return true;
  }
  return false;
};
export const isMutationUncertain = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") {
    return true;
  }
  if (result.kind === "hostFailure") {
    return result.failure.executionPhase !== "NOT_STARTED";
  }
  if (uncertainOutcome(result.value.outcome.data)) {
    return true;
  }
  return [
    "PREPARATION_STATE_UNKNOWN",
    "ATTEMPT_ADMISSION_UNKNOWN",
    "ATTEMPT_UNRESOLVED",
    "DISMISS_STATE_UNKNOWN",
    "RETAIN_STATE_UNKNOWN",
  ].includes(result.value.outcome.tag);
};

/** An access refusal clears only the cache for the read that received it. */
export const isDisclosureRefused = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") {
    return false;
  }
  const notice = resultNotice(result);
  return (
    notice.kind === "diagnostic" &&
    [
      "ACCESS_RESOURCE_UNAVAILABLE",
      "RESOURCE_UNAVAILABLE",
      "WEB_HOST_SESSION_REJECTED",
      "WEB_HOST_SESSION_FORBIDDEN",
    ].includes(notice.diagnostic.id)
  );
};
