import { localNotice } from "../api/notices";
import type { Notice } from "../api/notices";
import { useEffect, useState } from "react";
import { resultNotice, type DefinitionPayload, v3 } from "../api/v3";
import { webV3WireContractFingerprint } from "../generated/contracts/web-v3.endpoint-catalog";

export const useDefinition = (sessionEpoch: number) => {
  const [definition, setDefinition] = useState<DefinitionPayload | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void v3.definition(controller.signal).then((result) => {
      if (controller.signal.aborted) {
        return;
      }
      const payload =
        result.kind === "outcome" && result.value.outcome.tag === "DESCRIBED"
          ? result.value.outcome.data
          : null;
      if (payload === null) {
        setDefinition(null);
        setMessage(resultNotice(result));
      } else if (payload.webFingerprint === webV3WireContractFingerprint) {
        setDefinition(payload);
        setMessage(null);
      } else {
        setDefinition(null);
        setMessage(localNotice("definitionMismatch"));
      }
    });
    return () => {
      controller.abort();
    };
    // lint-exception: LX-0017
    // oxlint-disable-next-line react/exhaustive-effect-dependencies
  }, [sessionEpoch]);

  return { definition, message };
};
