import type { AdvisoryReview, PreparationDetails, Receipt, WebV2Response } from "../../api/v2";
import type { DeliveryState } from "../../domain/operationReducer";

export const nextOperationId = (): string => crypto.randomUUID();

export const isLocked = (delivery: DeliveryState): boolean =>
  delivery === "PREPARING" ||
  delivery === "PREPARATION_UNKNOWN" ||
  delivery === "RETAINED_FOR_RECOVERY" ||
  delivery === "SUBMITTING" ||
  delivery === "OUTCOME_UNKNOWN";

export const prepared = (
  response: WebV2Response<"command.prepare">,
): { details: PreparationDetails; review: AdvisoryReview } | null =>
  response.outcome.tag === "PREPARED" ? response.outcome.data : null;

export const acceptedReceipt = (response: WebV2Response<"command.execute">): Receipt | null => {
  const outcome = response.outcome;
  if (outcome.tag === "OBSERVED_ACCEPTED") return outcome.data.receipt;
  if (outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED") {
    return outcome.data.execution.receipt;
  }
  return null;
};
