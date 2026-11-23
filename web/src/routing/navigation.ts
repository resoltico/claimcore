const listeners = new Set<() => void>();
const navigationLocks = new Set<object>();
type DepartureGuard = (proceed: () => void, destination?: string) => boolean;
const blockers = new Map<object, { guard: DepartureGuard; priority: number }>();
let departureEpoch = 0;
const permittedDeparture = (
  proceed: () => void,
  destination?: string,
  visited = new Set<object>(),
  epoch = departureEpoch,
): boolean => {
  if (epoch !== departureEpoch) {
    return false;
  }
  const entry = [...blockers.entries()]
    .reverse()
    .sort((a, b) => b[1].priority - a[1].priority)
    .find(([key]) => !visited.has(key));
  if (entry === undefined) {
    return true;
  }
  const [key, { guard }] = entry;
  const next = () => permittedDeparture(proceed, destination, new Set([...visited, key]), epoch);
  return (
    guard(() => {
      if (next()) {
        proceed();
      }
    }, destination) && next()
  );
};

type Entry = { group: string; index: number; key: string };
type Traversal = {
  replayDispatched: boolean;
  origin: Entry;
  target: Entry;
  position: DeparturePosition | null;
  phase: "RESTORING" | "AWAITING" | "APPROVED" | "REPLAYING" | "CANCELLED";
};
const stateKey = "claimcoreNavigation";
let pending: Traversal | null = null;
const isHistoryObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const historyRecord = (): Record<string, unknown> => {
  const state: unknown = window.history.state;
  return isHistoryObject(state) ? state : {};
};
const entry = (): Entry | null => {
  const value = historyRecord()[stateKey];
  if (
    isHistoryObject(value) &&
    typeof value["group"] === "string" &&
    typeof value["key"] === "string" &&
    typeof value["index"] === "number" &&
    Number.isSafeInteger(value["index"])
  ) {
    return { group: value["group"], key: value["key"], index: value["index"] };
  }
  return null;
};
const initialEntry = (): Entry => ({
  group: crypto.randomUUID(),
  index: 0,
  key: crypto.randomUUID(),
});
const stamp = (value: Entry) => ({ ...historyRecord(), [stateKey]: value });
let active: Entry | null = null;
export const cancelPendingNavigation = (ownerChanged = false) => {
  if (
    pending !== null &&
    (ownerChanged || (pending.phase !== "REPLAYING" && pending.phase !== "APPROVED"))
  ) {
    pending.phase = "CANCELLED";
    // A queued go() cannot be unscheduled. Retain its cancellation until compensation arrives.
    if (!pending.replayDispatched && entry()?.key === pending.origin.key) {
      pending = null;
    }
  }
};
export const setNavigationBlocker = (
  next: (proceed: () => void, destination?: string) => boolean,
  priority = 1,
) => {
  const key = {};
  blockers.set(key, { guard: next, priority });
  return () => {
    blockers.delete(key);
    queueMicrotask(() => {
      if (blockers.size === 0) {
        cancelPendingNavigation();
      }
    });
  };
};
type FocusRequest = { id: string | null; scrollY: number | null };
type DeparturePosition = { key: string; epoch: number; focus: FocusRequest };
let requestedFocus: FocusRequest | null = null;
let focusActivation = { id: null as string | null, revision: 0 };
let routeRevision = 0;
const positions = new Map<string, FocusRequest>();
let focusOwner: string | null = null;
/** Return focus is ephemeral and never crosses a verified workspace owner. */
export const setRouteFocusOwner = (owner: string | null) => {
  if (focusOwner !== owner) {
    focusOwner = owner;
    departureEpoch += 1;
    positions.clear();
    requestedFocus = null;
    cancelPendingNavigation(true);
  }
};
const capturePosition = (returnTo?: string): DeparturePosition | null => {
  if (active === null || focusOwner === null) {
    return null;
  }
  const element = document.activeElement;
  return {
    key: active.key,
    epoch: departureEpoch,
    focus: {
      id: returnTo ?? (element instanceof HTMLElement && element.id !== "" ? element.id : null),
      scrollY: window.scrollY,
    },
  };
};
const rememberPosition = (position: DeparturePosition | null) => {
  if (position !== null && position.key === active?.key && position.epoch === departureEpoch) {
    positions.set(position.key, position.focus);
  }
};
export const takeRouteFocus = (): FocusRequest | null => {
  const target = requestedFocus;
  requestedFocus = null;
  return target;
};
let current =
  typeof window === "undefined" ? "/cases" : window.location.pathname + window.location.search;
const publish = () => {
  active = entry();
  const path = window.location.pathname + window.location.search;
  if (path !== current) {
    routeRevision += 1;
    current = path;
  }
  listeners.forEach((listener) => {
    listener();
  });
};
const publishTraversal = () => {
  const target = entry();
  requestedFocus = target === null ? null : (positions.get(target.key) ?? null);
  publish();
};
export const guardedDeparture = (proceed: () => void, destination?: string): boolean => {
  if (pending !== null || navigationLocks.size > 0 || !permittedDeparture(proceed, destination)) {
    return false;
  }
  proceed();
  return true;
};
const unload = (event: BeforeUnloadEvent) => {
  if (navigationLocks.size > 0 || blockers.size > 0) {
    event.preventDefault();
  }
};
const replay = (transition: Traversal) => {
  if (
    pending !== transition ||
    transition.phase === "CANCELLED" ||
    transition.phase === "REPLAYING"
  ) {
    return;
  }
  transition.phase = "APPROVED";
  const here = entry();
  if (here?.key === transition.origin.key) {
    transition.phase = "REPLAYING";
    transition.replayDispatched = true;
    window.history.go(transition.target.index - here.index);
  }
};
const settleTraversal = (transition: Traversal, target: Entry | null) => {
  if (
    target?.group !== transition.origin.group ||
    (target.index === transition.origin.index && target.key !== transition.origin.key)
  ) {
    pending = null;
    publishTraversal();
  } else if (transition.phase === "REPLAYING" && target.key === transition.target.key) {
    rememberPosition(transition.position);
    pending = null;
    publishTraversal();
  } else if (target.key === transition.origin.key) {
    switch (transition.phase) {
      case "CANCELLED":
        pending = null;
        break;
      case "APPROVED":
        replay(transition);
        break;
      case "RESTORING":
        transition.phase = "AWAITING";
        break;
      default:
        break;
    }
  } else {
    const destination = transition.phase === "REPLAYING" ? transition.target : transition.origin;
    window.history.go(destination.index - target.index);
  }
};
if (typeof window !== "undefined") {
  active = entry() ?? initialEntry();
  window.history.replaceState(stamp(active), "");
  window.addEventListener("beforeunload", unload);
  window.addEventListener("popstate", () => {
    const target = entry();
    const path = window.location.pathname + window.location.search;
    if (pending !== null) {
      settleTraversal(pending, target);
      return;
    }
    const position = capturePosition();
    // Unknown entries have no provable delta. Never repair or truncate their history.
    if (active === null || target?.group !== active.group || target.key === active.key) {
      rememberPosition(position);
      publishTraversal();
      return;
    }
    const transition: Traversal = {
      replayDispatched: false,
      origin: active,
      target,
      position,
      phase: "RESTORING",
    };
    if (
      navigationLocks.size === 0 &&
      permittedDeparture(() => {
        replay(transition);
      }, path)
    ) {
      rememberPosition(position);
      publishTraversal();
      return;
    }
    pending = transition;
    if (navigationLocks.size > 0) {
      transition.phase = "CANCELLED";
    }
    window.history.go(active.index - target.index);
  });
}
export const subscribeRoute = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
export const routeSnapshot = () => current;
export const routeRevisionSnapshot = () => routeRevision;
export const focusActivationSnapshot = () => focusActivation;
export const acquireNavigationLock = (): (() => void) => {
  const lease = {};
  navigationLocks.add(lease);
  return () => {
    navigationLocks.delete(lease);
  };
};
export const navigate = (
  path: string,
  replace = false,
  focusId?: string,
  scrollY?: number,
  returnFocusId?: string,
): boolean => {
  const target = new URL(path, window.location.origin);
  if (target.origin !== window.location.origin) {
    return false;
  }
  const destination = target.pathname + target.search;
  if (destination === current && focusId === undefined) {
    return true;
  }
  const position = capturePosition(returnFocusId);
  return guardedDeparture(() => {
    requestedFocus = { id: focusId ?? null, scrollY: scrollY ?? null };
    if (focusId !== undefined) {
      focusActivation = { id: focusId, revision: focusActivation.revision + 1 };
    }
    if (destination === current) {
      publish();
      return;
    }
    rememberPosition(position);
    const origin = entry() ?? initialEntry();
    const next = replace
      ? origin
      : { ...origin, index: origin.index + 1, key: crypto.randomUUID() };
    window.history[replace ? "replaceState" : "pushState"](stamp(next), "", destination);
    publish();
  }, destination);
};
/** A disclosure replacement retains the current entry identity and unrelated state. */
export const replaceDisclosureRoute = (path: string): void => {
  const target = new URL(path, window.location.origin);
  if (target.origin === window.location.origin) {
    window.history.replaceState(window.history.state, "", target.pathname + target.search);
    publish();
  }
};
