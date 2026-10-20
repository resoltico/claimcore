import { useCallback, useState } from "react";
import type { PreparedRequest } from "../../protocol/preparedRequest";
import {
  beginMutation,
  completeMutation,
  interruptMutation,
  mutationUnresolved,
  type MutationLifecycle,
  type MutationEndpoint,
} from "../../interaction/mutationLifecycle";
export const useMutationLifecycle = <K extends MutationEndpoint>() => {
  const [mutation, setMutation] = useState<MutationLifecycle<K>>({
    phase: "EDITING",
    prepared: null,
  });
  const prepared = mutation.phase === "ACCEPTED" ? null : mutation.prepared;
  const bytes = prepared?.bytes ?? null;
  const busy = mutation.phase === "SUBMITTING";
  const uncertain = mutationUnresolved(mutation);
  const setPrepared = (value: PreparedRequest<K>) => {
    setMutation((prior) =>
      prior.phase === "SUBMITTING" || mutationUnresolved(prior)
        ? prior
        : { phase: "REVIEWING", prepared: value },
    );
  };
  const clearPrepared = () => {
    setMutation((prior) =>
      prior.phase === "SUBMITTING" || mutationUnresolved(prior)
        ? prior
        : { phase: "EDITING", prepared: null },
    );
  };
  const interrupt = useCallback(() => {
    setMutation(interruptMutation);
  }, []);
  const begin = (requestId: number) => {
    setMutation((prior) => beginMutation(prior, requestId));
  };
  const complete = (
    requestId: number,
    attempt: Parameters<typeof completeMutation>[2],
    evidence: Parameters<typeof completeMutation>[3],
  ) => {
    setMutation((prior) => completeMutation(prior, requestId, attempt, evidence));
  };
  return {
    prepared,
    bytes,
    busy,
    uncertain,
    setPrepared,
    clearPrepared,
    interrupt,
    begin,
    complete,
  };
};
