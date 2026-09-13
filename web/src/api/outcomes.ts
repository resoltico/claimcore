import type { ApiResult, EndpointOutcome } from "../generated/convergence/web-v2.types";

type Download = { blob: Blob; filename: string };

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const nestedMessage = (data: Record<string, unknown>, key: string): string | null => {
  const value = data[key];
  return isObject(value) && typeof value["message"] === "string" ? value["message"] : null;
};

export const resultMessage = (result: ApiResult<EndpointOutcome | Download>): string => {
  if (result.kind === "deliveryFailure") return result.message;
  if (result.kind === "hostFailure") return `${result.failure.code}: ${result.failure.message}`;
  const value = result.value;
  if (!("outcome" in value) || !isObject(value.outcome.data))
    return "The operation did not complete.";
  const data = value.outcome.data;
  const message =
    nestedMessage(data, "rejection") ??
    nestedMessage(data, "fault") ??
    nestedMessage(data, "message");
  if (message !== null) return message;
  switch (value.outcome.tag) {
    case "DISMISSED":
      return "Preparation dismissed.";
    case "ALREADY_DISMISSED":
      return "Preparation was already dismissed.";
    case "RETAINED":
      return "Recovery material retained.";
    case "EXISTING":
      return "Recovery material was already retained.";
    default:
      return "The operation did not complete.";
  }
};

export const isMutationUncertain = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") return true;
  if (result.kind === "hostFailure") return result.failure.executionPhase !== "NOT_STARTED";
  return [
    "PREPARATION_STATE_UNKNOWN",
    "ATTEMPT_ADMISSION_UNKNOWN",
    "ATTEMPT_UNRESOLVED",
    "DISMISS_STATE_UNKNOWN",
    "RETAIN_STATE_UNKNOWN",
  ].includes(result.value.outcome.tag);
};
