import { useState } from "react";
import { useEndpoint } from "./useEndpoint";
export const useOperationObservation = (initialOperationId = "") => {
  const [operationId, setOperationId] = useState(initialOperationId);
  const [submitted, setSubmitted] = useState<string | null>(
    initialOperationId === "" ? null : initialOperationId,
  );
  const query = useEndpoint(
    "operation.observe",
    { operationId: submitted ?? "" },
    submitted !== null,
  );
  const changeOperationId = (value: string): void => {
    setOperationId(value);
    setSubmitted(null);
  };
  const observe = (): Promise<void> => {
    if (operationId !== "") {
      if (submitted === operationId && !query.loading) {
        query.reload();
      } else if (submitted !== operationId) {
        setSubmitted(operationId);
      }
    }
    return Promise.resolve();
  };
  return {
    operationId,
    setOperationId: changeOperationId,
    receipt:
      submitted !== null && query.value?.outcome.kind === "SUCCEEDED"
        ? query.value.outcome.data
        : null,
    message: submitted === null ? null : query.message,
    loading: submitted !== null && query.loading,
    observe,
  };
};
