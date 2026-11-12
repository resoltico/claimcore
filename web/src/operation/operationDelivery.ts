import { sameOwnerScope } from "../protocol/ownerScope";
import type { Notice } from "../protocol/notices";
import { operationSource, type OperationDelivery, type EffectIdentity } from "./operationTypes";
export const deliveryBusy = ({ phase }: OperationDelivery): boolean =>
  phase.kind === "PREVIEWING" ||
  phase.kind === "PREPARING" ||
  phase.kind === "SUBMITTING" ||
  (phase.kind === "UNRESOLVED" && phase.pending !== null);
export const deliveryEditable = ({ phase }: OperationDelivery): boolean =>
  phase.kind === "EDITING" || phase.kind === "REVIEWING";
export const sameRequest = (left: EffectIdentity, right: EffectIdentity): boolean =>
  sameOwnerScope(left.owner, right.owner) &&
  left.id === right.id &&
  left.contextGeneration === right.contextGeneration &&
  left.credential === right.credential &&
  left.target === right.target;
export const deliveryCompleting = (
  delivery: OperationDelivery,
  identity: EffectIdentity,
  current: boolean,
): boolean => {
  const { phase } = delivery;
  if (!sameOwnerScope(delivery.owner, identity.owner)) {
    return false;
  }
  if (phase.kind === "SUBMITTING") {
    return sameRequest(phase.effect, identity);
  }
  if (phase.kind === "UNRESOLVED") {
    return phase.pending !== null && sameRequest(phase.pending.identity, identity);
  }
  return (
    current &&
    (phase.kind === "PREVIEWING" || phase.kind === "PREPARING") &&
    sameRequest(phase.effect, identity)
  );
};
export const invalidateDelivery = (delivery: OperationDelivery): OperationDelivery => {
  const { phase } = delivery;
  if (phase.kind === "SUBMITTING" || phase.kind === "ACCEPTED") {
    return delivery;
  }
  return {
    ...delivery,
    phase:
      phase.kind === "UNRESOLVED"
        ? { ...phase, pending: phase.pending?.kind === "EXECUTE" ? phase.pending : null }
        : { kind: "EDITING", exposed: operationSource(phase) },
  };
};
export const deliveryStatus = (notice: Notice): OperationDelivery["notice"] =>
  notice.kind === "local" || notice.kind === "pending" || notice.kind === "accepted"
    ? notice
    : null;
