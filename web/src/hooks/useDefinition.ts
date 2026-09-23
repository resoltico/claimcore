import { localNotice } from "../api/notices";
import type { Notice } from "../api/notices";
import { useEffect, useState } from "react";
import { resultNotice, type DefinitionPayload, v2 } from "../api/v2";
import { webV2WireContractFingerprint } from "../generated/convergence/web-v2.endpoint-catalog";

export const useDefinition = (sessionEpoch: number) => {
  const [definition, setDefinition] = useState<DefinitionPayload | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void v2.definition(controller.signal).then((result) => {
      if (controller.signal.aborted) return;
      const payload =
        result.kind === "outcome" && result.value.outcome.tag === "DESCRIBED"
          ? result.value.outcome.data
          : null;
      if (payload === null) {
        setDefinition(null);
        setMessage(resultNotice(result));
      } else if (payload.webFingerprint !== webV2WireContractFingerprint) {
        setDefinition(null);
        setMessage(localNotice("definitionMismatch"));
      } else {
        setDefinition(payload);
        setMessage(null);
      }
    });
    return () => controller.abort();
  }, [sessionEpoch]);

  return { definition, message };
};
