import { useState } from "react";
import type { Notice } from "../api/notices";
import { resultNotice, v2, type Receipt } from "../api/v2";

export const useOperationObservation = (token: string) => {
  const [operationId, setOperationId] = useState("");
  const [receipt, setReceipt] = useState<Receipt | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);
  const [notObserved, setNotObserved] = useState(false);
  const [loading, setLoading] = useState(false);
  const observe = async (): Promise<void> => {
    setLoading(true);
    setNotObserved(false);
    setReceipt(null);
    setMessage(null);
    const result = await v2.observe(operationId, token);
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
    setLoading(false);
    setReceipt(found);
    setNotObserved(missing);
    setMessage(found === null && !missing ? resultNotice(result) : null);
  };
  return { operationId, setOperationId, receipt, message, notObserved, loading, observe };
};
