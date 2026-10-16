import { useCallback, useRef, useState } from "react";
import type { Notice } from "../../api/notices";

// A refusal invalidates the whole disclosure snapshot, independently of the authored draft.
// A further refusal clears that snapshot too, and waits for explicit reload instead of looping.
export const useAuthorityDisclosure = () => {
  const [snapshot, setSnapshot] = useState<{
    revision: number;
    active: boolean;
    notice: Notice | null;
  }>({ revision: 0, active: true, notice: null });
  const { revision } = snapshot;
  const control = useRef({ revision: 0, revalidating: false, context: false, listing: false });
  const invalidate = useCallback((force = false, notice: Notice | null = null) => {
    const { current } = control;
    control.current = {
      revision: current.revision + 1,
      revalidating: true,
      context: false,
      listing: false,
    };
    setSnapshot({
      revision: control.current.revision,
      active: force || !current.revalidating,
      notice,
    });
  }, []);
  const refused = useCallback(
    (notice?: Notice) => {
      if (control.current.revision === revision) {
        invalidate(false, notice);
      }
    },
    [invalidate, revision],
  );
  const restored = useCallback(
    (source: "context" | "listing") => {
      if (control.current.revision === revision) {
        control.current[source] = true;
        if (control.current.context && control.current.listing) {
          control.current.revalidating = false;
        }
      }
    },
    [revision],
  );
  return { ...snapshot, invalidate, refused, restored };
};
