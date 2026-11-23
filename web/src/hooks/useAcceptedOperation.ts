import { useCallback, useLayoutEffect, useRef, useState } from "react";
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
export const useAcceptedOperation = (current?: RequesterDocument | null, denied = false) => {
  const { state } = useSessionContext();
  const context = useRequesterDocument();
  const requester = current === undefined ? context : current;
  const [previous, setPrevious] = useState<VerifiedOwner | null>(null);
  const owner = denied ? null : retainOwner(previous, state, requester);
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
    path: string;
  } | null>(null);
  const path = location.pathname + location.search;
  const previousPath = useRef(path);
  useLayoutEffect(() => {
    if (previousPath.current !== path) {
      previousPath.current = path;
      setAccepted((receipt) => (receipt?.path === path ? receipt : null));
    }
  }, [path]);
  const visible =
    requester !== null && accepted?.owner === owner && accepted.path === path ? accepted : null;
  const focused = useRef<string | null>(null);
  const shouldFocus = useCallback(() => {
    if (
      visible === null ||
      latestOwner.current !== visible.owner ||
      focused.current === visible.operationId
    ) {
      return false;
    }
    focused.current = visible.operationId;
    return true;
  }, [visible]);
  return {
    owner,
    shouldFocus,
    operationId: visible?.operationId ?? null,
    messageId: visible?.messageId,
    clear: () => {
      setAccepted(null);
    },
    accept: (
      operationId: string,
      messageId?: string,
      target = location.pathname + location.search,
    ) => {
      if (owner !== null && latestOwner.current === owner) {
        setAccepted({ owner, operationId, messageId, path: target });
        return true;
      }
      return false;
    },
  };
};
