import { useEffect, useRef, useState } from "react";
import type { Notice } from "../api/notices";
import { resultNotice, v3, type Receipt } from "../api/v3";

const observation = (result: Awaited<ReturnType<typeof v3.observe>>) => {
  const found =
    result.kind === "outcome" &&
    result.value.outcome.tag === "SUCCEEDED" &&
    result.value.outcome.data.tag === "FOUND"
      ? result.value.outcome.data.receipt
      : null;
  const missing =
    result.kind === "outcome" &&
    result.value.outcome.tag === "SUCCEEDED" &&
    result.value.outcome.data.tag === "NOT_FOUND";
  return { found, missing };
};

type ObservationFeedback = {
  receipt: Receipt | null;
  message: Notice | null;
  notObserved: boolean;
  loading: boolean;
};
const emptyFeedback: ObservationFeedback = {
  receipt: null,
  message: null,
  notObserved: false,
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
    const result = await v3.observe(operationId, token, current.signal);
    if (current.signal.aborted) {
      return;
    }
    controller.current = null;
    const { found, missing } = observation(result);
    setFeedback({
      receipt: found,
      notObserved: missing,
      loading: false,
      message: found === null && !missing ? resultNotice(result) : null,
    });
  };
  return {
    operationId,
    setOperationId: changeOperationId,
    ...feedback,
    observe,
  };
};
