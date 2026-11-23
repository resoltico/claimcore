import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { useSessionContext } from "../session/SessionContext";
import { useRequesterContext } from "./useRequesterContext";

/** Retain identity, never a private receipt document, across read invalidations. */
export const useAcceptedOperation = () => {
  const { state } = useSessionContext();
  const requester = useRequesterContext();
  const scope =
    state.kind === "authenticated" && requester !== null
      ? JSON.stringify([
          location.origin,
          state.epoch,
          state.token,
          requester.subject.installationId,
          requester.subject.actorId,
        ])
      : null;
  const owner = useMemo(() => (scope === null ? null : { scope }), [scope]);
  const latestOwner = useRef(owner);
  useLayoutEffect(() => {
    latestOwner.current = owner;
    return () => {
      latestOwner.current = null;
    };
  }, [owner]);
  const [accepted, setAccepted] = useState<{
    owner: NonNullable<typeof owner>;
    operationId: string;
    messageId: string | undefined;
  } | null>(null);
  const visible = accepted?.owner === owner ? accepted : null;
  return {
    operationId: visible?.operationId ?? null,
    messageId: visible?.messageId,
    clear: () => {
      setAccepted(null);
    },
    accept: (operationId: string, messageId?: string) => {
      if (owner !== null && latestOwner.current === owner) {
        setAccepted({ owner, operationId, messageId });
      }
    },
  };
};
