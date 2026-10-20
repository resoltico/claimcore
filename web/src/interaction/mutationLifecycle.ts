import type { endpoints } from "@contracts/client";
import type { PreparedRequest } from "../protocol/preparedRequest";
import {
  accumulateKnowledge,
  type AttemptKnowledge,
  type ResolutionEvidence,
} from "../protocol/delivery";
export type MutationEndpoint = Extract<(typeof endpoints)[number], { mutates: true }>["id"];
export type MutationLifecycle<K extends MutationEndpoint> =
  | Readonly<{ phase: "EDITING" | "DEFINITELY_REJECTED"; prepared: PreparedRequest<K> | null }>
  | Readonly<{ phase: "PREVIEWING"; prepared: PreparedRequest<K>; requestId: number }>
  | Readonly<{ phase: "REVIEWING" | "ACCEPTED" | "OUTCOME_UNKNOWN"; prepared: PreparedRequest<K> }>
  | Readonly<{
      phase: "SUBMITTING";
      prepared: PreparedRequest<K>;
      requestId: number;
      prior: "NOT_SENT" | "UNCONFIRMED";
    }>;
export const mutationUnresolved = <K extends MutationEndpoint>(
  state: MutationLifecycle<K>,
): boolean =>
  state.phase === "OUTCOME_UNKNOWN" ||
  (state.phase === "SUBMITTING" && state.prior === "UNCONFIRMED");
export const beginMutation = <K extends MutationEndpoint>(
  state: MutationLifecycle<K>,
  requestId: number,
): MutationLifecycle<K> =>
  (state.phase === "REVIEWING" ||
    state.phase === "OUTCOME_UNKNOWN" ||
    state.phase === "DEFINITELY_REJECTED") &&
  state.prepared !== null
    ? {
        phase: "SUBMITTING",
        prepared: state.prepared,
        requestId,
        prior: mutationUnresolved(state) ? "UNCONFIRMED" : "NOT_SENT",
      }
    : state;
export const completeMutation = <K extends MutationEndpoint>(
  state: MutationLifecycle<K>,
  requestId: number,
  attempt: AttemptKnowledge,
  evidence: ResolutionEvidence,
): MutationLifecycle<K> => {
  if (
    (state.phase !== "SUBMITTING" && state.phase !== "PREVIEWING") ||
    state.requestId !== requestId
  ) {
    return state;
  }
  const knowledge = accumulateKnowledge(
    state.phase === "SUBMITTING" ? state.prior : "NOT_SENT",
    attempt,
    evidence,
  );
  if (knowledge === "UNCONFIRMED") {
    return { phase: "OUTCOME_UNKNOWN", prepared: state.prepared };
  }
  return {
    phase: knowledge === "ACCEPTED" ? "ACCEPTED" : "DEFINITELY_REJECTED",
    prepared: state.prepared,
  };
};
export const interruptMutation = <K extends MutationEndpoint>(
  state: MutationLifecycle<K>,
): MutationLifecycle<K> =>
  state.phase === "SUBMITTING" ? { phase: "OUTCOME_UNKNOWN", prepared: state.prepared } : state;
