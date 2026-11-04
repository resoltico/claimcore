import { useLayoutEffect, useMemo, useState } from "react";
import { subscribeReadRetries } from "../api/readRetryNotice";
import { useCredential, useSessionContext } from "../session/SessionContext";
import type { RequesterState_ACTIVE } from "@contracts/client";
export const useReadRetrying = (requester: RequesterState_ACTIVE | null) => {
  const credential = useCredential();
  const { state: session } = useSessionContext();
  const active = session.kind === "authenticated";
  const installationId = requester?.installationId;
  const actorId = requester?.actorId;
  const owner = useMemo(
    () => (active ? { credential, epoch: session.epoch, installationId, actorId } : null),
    [active, credential, session.epoch, installationId, actorId],
  );
  const [notice, setNotice] = useState<{ owner: typeof owner; waiting: boolean } | null>(null);
  useLayoutEffect(() => {
    if (owner === null) {
      return;
    }
    return subscribeReadRetries(credential, (waiting) => {
      setNotice({ owner, waiting });
    });
  }, [credential, owner]);
  return notice !== null && notice.owner === owner && notice.waiting;
};
