import type { HumanDocument } from "@contracts/client";
import { apiEndpoints } from "../api/service";
import { useRequesterContext } from "./useRequesterContext";
import { useLayoutEffect, useState } from "react";
import { useEndpointCache } from "./EndpointProvider";
import { useEndpoint } from "./useEndpoint";
const useObservedAttachmentReceipt = (receipt: HumanDocument | null) => {
  const cache = useEndpointCache();
  useLayoutEffect(() => {
    if (receipt !== null) {
      cache?.observeAttachmentReceipt(receipt);
    }
  }, [cache, receipt]);
  return receipt !== null && cache?.permitsAttachmentReceipt(receipt) !== false ? receipt : null;
};

export const useOperationObservation = (initialOperationId = "") => {
  const [operationId, setOperationId] = useState(initialOperationId);
  const [submitted, setSubmitted] = useState<string | null>(
    initialOperationId === "" ? null : initialOperationId,
  );
  const requester = useRequesterContext();
  const installationId = requester?.subject.installationId;
  const query = useEndpoint(
    apiEndpoints.operationGet,
    { operationId: submitted ?? "" },
    submitted !== null && installationId !== undefined,
    JSON.stringify([installationId, requester?.subject.actorId]),
    installationId,
  );
  const receipt = useObservedAttachmentReceipt(
    submitted !== null && query.value?.outcome.kind === "ACCEPTED"
      ? query.value.outcome.document
      : null,
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
    receipt,
    message: submitted === null ? null : query.message,
    loading: submitted !== null && query.loading,
    observe,
  };
};
