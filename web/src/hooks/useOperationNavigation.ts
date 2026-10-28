import { useCallback, useEffect, useRef } from "react";
import type { OperationModel } from "./operation/useOperation";
import { setNavigationBlocker } from "../routing/navigation";
/** The existing discard/recovery dialog owns consent even for top-level navigation. */
export const useOperationNavigation = (
  model: OperationModel,
  onClose?: () => void,
  scope?: string,
) => {
  const { dirty, priorUncertain, requestDiscard, phase, prepared } = model;
  const pending = useRef<(() => void) | null>(null);
  const release = useRef<(() => void) | null>(null);
  useEffect(() => {
    if (phase === "ACCEPTED" || (!dirty && !priorUncertain && prepared === null)) {
      return;
    }
    const dispose = setNavigationBlocker((proceed, destination) => {
      if (
        scope !== undefined &&
        destination !== undefined &&
        (destination === scope || destination.startsWith(`${scope}/`))
      ) {
        return true;
      }
      pending.current = proceed;
      requestDiscard("LEAVE");
      return false;
    });
    release.current = dispose;
    return dispose;
  }, [dirty, priorUncertain, requestDiscard, phase, scope, prepared]);
  useEffect(() => {
    if (model.pendingDiscard === null) {
      pending.current = null;
    }
  }, [model.pendingDiscard]);
  const committed = useCallback(() => {
    release.current?.();
  }, []);
  const close = useCallback(() => {
    release.current?.();
    const proceed = pending.current;
    pending.current = null;
    if (proceed === null) {
      onClose?.();
    } else {
      proceed();
    }
  }, [onClose]);
  return { close, committed };
};
