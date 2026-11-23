import { useLayoutEffect, useMemo, useState } from "react";
import { subscribeBusinessDates } from "../api/businessDateObservation";
import { useEndpointCache } from "./EndpointProvider";
import { useCredential, useSessionContext } from "../session/SessionContext";
import type { ActiveRequester } from "./useRequesterContext";

/** A changed server business date invalidates dependent observations. */
export const useBusinessDate = (requester: ActiveRequester | null) => {
  const cache = useEndpointCache();
  const credential = useCredential();
  const { epoch } = useSessionContext().state;

  const installationId = requester?.subject.installationId;
  const actorId = requester?.subject.actorId;
  const date = requester?.basis.observation.businessDate;
  const available = requester !== null;
  const owner = useMemo(
    () => (available ? { credential, epoch, installationId, actorId } : null),
    [credential, epoch, installationId, actorId, available],
  );
  const [change, setChange] = useState<{ owner: typeof owner; date: string } | null>(null);
  useLayoutEffect(() => {
    if (requester === null) {
      return;
    }
    let latest = date;
    return subscribeBusinessDates({
      credential,
      installationId: requester.subject.installationId,
      actorId: requester.subject.actorId,
      observed: (observed) => {
        if (observed !== latest) {
          latest = observed;
          setChange({ owner, date: observed });
          cache?.invalidate();
        }
      },
    });
  }, [credential, date, requester, owner, cache]);
  return {
    changedDate: change !== null && change.owner === owner ? change.date : null,
    refresh: () => {
      setChange(null);
      cache?.invalidate();
    },
  };
};
