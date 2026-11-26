import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { useCompact } from "../hooks/useCompact";
import { routeRevisionSnapshot } from "../routing/navigation";

const focusControl = (id: string | undefined) => {
  const control = id === undefined ? null : document.getElementById(id);
  return control?.closest('[hidden],[inert],[aria-hidden="true"],:disabled') === null
    ? control
    : null;
};

export const workspaceFindId = (compact: boolean) =>
  compact ? "workspace-phone-find" : "workspace-find";

export const useWorkspaceFind = (owner: string | null, canListCases: boolean, locked: boolean) => {
  const scope = canListCases && !locked ? owner : null;
  const fallback = workspaceFindId(useCompact());
  const [invocation, setInvocation] = useState<{
    owner: string;
    route: number;
    invoker: string | undefined;
  } | null>(null);
  const focusOwner = useRef<string | null>(null);
  useLayoutEffect(() => {
    focusOwner.current = scope;
    return () => {
      focusOwner.current = null;
    };
  }, [scope]);
  const onFind = useCallback(
    (invoker?: string) => {
      if (scope === null) {
        return;
      }
      focusControl(invoker)?.focus();
      setInvocation({ owner: scope, route: routeRevisionSnapshot(), invoker });
    },
    [scope],
  );
  const active =
    invocation?.owner === scope && invocation.route === routeRevisionSnapshot() ? invocation : null;
  if (invocation !== null && active === null) {
    setInvocation(null);
  }
  const dismiss = () => {
    setInvocation(null);
    if (active?.invoker === undefined) {
      return;
    }
    requestAnimationFrame(() => {
      if (
        focusOwner.current !== active.owner ||
        active.route !== routeRevisionSnapshot() ||
        document.querySelector('[role="dialog"]') !== null
      ) {
        return;
      }
      const invoker = focusControl(active.invoker);
      invoker?.focus();
      if (invoker === null || document.activeElement !== invoker) {
        focusControl(fallback)?.focus();
      }
    });
  };
  return {
    onFind: scope === null ? undefined : onFind,
    open: active !== null,
    returnFocusId: active?.invoker ?? fallback,
    dismiss,
  };
};
