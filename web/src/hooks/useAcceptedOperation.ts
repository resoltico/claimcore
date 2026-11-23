import { useLayoutEffect, useRef, useState } from "react";
import { useSessionContext } from "../session/SessionContext";
import type { SessionState } from "../session/useSession";
import type { RequesterDocument } from "../protocol/types";
import { sameOwnerScope, type OwnerScope } from "../protocol/ownerScope";
import { isActiveRequester, useRequesterDocument } from "./useRequesterContext";

type VerifiedOwner = OwnerScope & { credential: string };

const retainOwner = (
  previous: VerifiedOwner | null,
  session: SessionState,
  requester: RequesterDocument | null,
) => {
  if (session.kind !== "authenticated") {
    return null;
  }
  const retained =
    previous?.sessionEpoch === session.epoch && previous.credential === session.token
      ? previous
      : null;
  if (requester === null) {
    return retained;
  }
  if (!isActiveRequester(requester)) {
    return null;
  }
  const scope = {
    serviceOrigin: location.origin,
    installationId: requester.subject.installationId,
    actorId: requester.subject.actorId,
    sessionEpoch: session.epoch,
  };
  return retained !== null && sameOwnerScope(retained, scope)
    ? retained
    : { ...scope, credential: session.token };
};

/** Mask feedback during a requester reread. A verified owner change retires it. */
export const useAcceptedOperation = () => {
  const { state } = useSessionContext();
  const requester = useRequesterDocument();
  const [previous, setPrevious] = useState<VerifiedOwner | null>(null);
  const owner = retainOwner(previous, state, requester);
  if (owner !== previous) {
    setPrevious(owner);
  }
  const latestOwner = useRef(owner);
  useLayoutEffect(() => {
    latestOwner.current = owner;
    return () => {
      latestOwner.current = null;
    };
  }, [owner]);
  const [accepted, setAccepted] = useState<{
    owner: VerifiedOwner;
    operationId: string;
    messageId: string | undefined;
  } | null>(null);
  const visible = requester !== null && accepted?.owner === owner ? accepted : null;
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
