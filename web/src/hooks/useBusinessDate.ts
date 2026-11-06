import { useEffect, useLayoutEffect, useMemo, useState } from "react";
import { subscribeBusinessDates } from "../api/businessDateObservation";
import { useEndpointCache } from "./EndpointProvider";
import { useCredential, useSessionContext } from "../session/SessionContext";
import type { RequesterState_ACTIVE } from "@contracts/client";

/** Focus refresh replaces polling; all dates remain server observations. */
export const useBusinessDate = (requester: RequesterState_ACTIVE | null) => {
  const cache = useEndpointCache();
  const credential = useCredential();
  const { epoch } = useSessionContext().state;

  const installationId = requester?.installationId;
  const actorId = requester?.actorId;
  const date = requester?.businessDate;
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
      installationId: requester.installationId,
      actorId: requester.actorId,
      observed: (observed) => {
        if (observed !== latest) {
          latest = observed;
          setChange({ owner, date: observed });
          cache?.invalidate();
        }
      },
    });
  }, [credential, date, requester, owner, cache]);
  useEffect(() => {
    if (requester === null) {
      return;
    }
    const resumed = () => {
      if (document.visibilityState === "visible") {
        cache?.invalidate();
      }
    };
    window.addEventListener("focus", resumed);
    document.addEventListener("visibilitychange", resumed);
    return () => {
      window.removeEventListener("focus", resumed);
      document.removeEventListener("visibilitychange", resumed);
    };
  }, [requester, cache]);
  return {
    changedDate: change !== null && change.owner === owner ? change.date : null,
    refresh: () => {
      setChange(null);
      cache?.invalidate();
    },
  };
};
