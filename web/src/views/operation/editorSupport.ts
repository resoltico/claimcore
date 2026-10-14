import type { Receipt, ServiceResponse } from "../../api/service";
import type { DeliveryState } from "../../domain/operationReducer";
export const nextOperationId = (): string => crypto.randomUUID();
export const isLocked = (delivery: DeliveryState): boolean =>
  delivery === "PREVIEWING" || delivery === "SUBMITTING" || delivery === "OUTCOME_UNKNOWN";
export const acceptedReceipt = (response: ServiceResponse<"command.execute">): Receipt | null =>
  response.outcome.tag === "ACCEPTED" || response.outcome.tag === "ALREADY_ACCEPTED"
    ? response.outcome.data
    : null;
