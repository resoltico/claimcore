import { useLayoutEffect, useRef, useState } from "react";
import { useSessionContext } from "../session/SessionContext";
import { useRequesterContext } from "./useRequesterContext";

/** Retain identity, never a private receipt document, across read invalidations. */
export const useAcceptedOperation = () => {
  const { state } = useSessionContext();
  const requester = useRequesterContext();
  const owner =
    state.kind === "authenticated" && requester !== null
      ? JSON.stringify([
          location.origin,
          state.epoch,
          state.token,
          requester.context.installationId,
          requester.context.actorId,
        ])
      : null;
  const latestOwner = useRef(owner);
  useLayoutEffect(() => {
    latestOwner.current = owner;
    return () => {
      latestOwner.current = null;
    };
  }, [owner]);
  const [accepted, setAccepted] = useState<{ owner: string; operationId: string } | null>(null);
  return {
    operationId: accepted !== null && accepted.owner === owner ? accepted.operationId : null,
    clear: () => {
      setAccepted(null);
    },
    accept: (operationId: string) => {
      if (owner !== null && latestOwner.current === owner) {
        setAccepted({ owner, operationId });
      }
    },
  };
};
