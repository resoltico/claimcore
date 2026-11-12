import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { acquireNavigationLock } from "../routing/navigation";

export type MutationLockChange = (locked: boolean, lease: object) => void;

/** Operation leases control only their workspace owner; stale release never unlocks another lease. */
export const useWorkspaceMutationLock = (owner: object | null) => {
  const active = useRef<{
    owner: object | null;
    leases: Set<object>;
    release: (() => void) | null;
  }>({ owner, leases: new Set(), release: null });
  const [snapshot, setSnapshot] = useState({ owner, locked: false });
  useLayoutEffect(() => {
    const previous = active.current;
    if (previous.owner !== owner) {
      previous.release?.();
      active.current = { owner, leases: new Set(), release: null };
    } else if (previous.leases.size > 0 && previous.release === null) {
      previous.release = acquireNavigationLock();
    }
    return () => {
      active.current.release?.();
      active.current.release = null;
    };
  }, [owner]);
  const change: MutationLockChange = useCallback(
    (locked, lease) => {
      const { current } = active;
      if (owner === null || current.owner !== owner) {
        return;
      }
      if (locked) {
        current.leases.add(lease);
      } else {
        current.leases.delete(lease);
      }
      const busy = current.leases.size > 0;
      if (busy && current.release === null) {
        current.release = acquireNavigationLock();
      } else if (!busy) {
        current.release?.();
        current.release = null;
      }
      setSnapshot((previous) =>
        previous.owner === owner && previous.locked === busy ? previous : { owner, locked: busy },
      );
    },
    [owner],
  );
  return { locked: owner !== null && snapshot.owner === owner && snapshot.locked, change };
};
