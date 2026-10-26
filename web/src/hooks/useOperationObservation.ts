import { useEffect, useRef, useState } from "react";
import type { Notice } from "../protocol/notices";
import { resultNotice, service, type OperationReceipt } from "../api/service";

type ObservationFeedback = {
  receipt: OperationReceipt | null;
  message: Notice | null;
  loading: boolean;
};
const emptyFeedback: ObservationFeedback = {
  receipt: null,
  message: null,
  loading: false,
};

export const useOperationObservation = (token: string, initialOperationId = "") => {
  const [operationId, setOperationId] = useState(initialOperationId);
  const controller = useRef<AbortController | null>(null);
  const [feedback, setFeedback] = useState(emptyFeedback);
  useEffect(
    () => () => {
      controller.current?.abort();
    },
    [],
  );
  const changeOperationId = (value: string): void => {
    controller.current?.abort();
    controller.current = null;
    setOperationId(value);
    setFeedback(emptyFeedback);
  };
  const observe = async (): Promise<void> => {
    if (controller.current !== null || operationId === "") {
      return;
    }
    const current = new AbortController();
    controller.current = current;
    setFeedback({ ...emptyFeedback, loading: true });
    const result = await service.observe(operationId, token, current.signal);
    if (current.signal.aborted) {
      return;
    }
    controller.current = null;
    const found =
      result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED"
        ? result.value.outcome.data
        : null;
    setFeedback({
      receipt: found,
      loading: false,
      message: found === null ? resultNotice(result) : null,
    });
  };
  return {
    operationId,
    setOperationId: changeOperationId,
    ...feedback,
    observe,
  };
};
