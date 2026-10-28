const listeners = new Set<() => void>();
let locked = false;
let blocker: ((proceed: () => void, destination?: string) => boolean) | null = null;
export const setNavigationBlocker = (
  next: (proceed: () => void, destination?: string) => boolean,
) => {
  blocker = next;
  return () => {
    if (blocker === next) {
      blocker = null;
    }
  };
};
let requestedFocus: string | null = null;
export const takeRouteFocus = (): string | null => {
  const target = requestedFocus;
  requestedFocus = null;
  return target;
};
let current =
  typeof window === "undefined" ? "/cases" : window.location.pathname + window.location.search;
const publish = () => {
  current = window.location.pathname + window.location.search;
  listeners.forEach((listener) => {
    listener();
  });
};
export const guardedDeparture = (proceed: () => void, destination?: string): boolean => {
  if (locked || blocker?.(proceed, destination) === false) {
    return false;
  }
  proceed();
  return true;
};
const unload = (event: BeforeUnloadEvent) => {
  if (locked) {
    event.preventDefault();
  }
};
if (typeof window !== "undefined") {
  window.addEventListener("beforeunload", unload);
  window.addEventListener("popstate", () => {
    const target = window.location.pathname + window.location.search;
    if (locked || blocker !== null) {
      window.history.pushState(null, "", current);
      if (!locked) {
        guardedDeparture(() => {
          window.history.replaceState(null, "", target);
          publish();
        });
      }
    } else {
      publish();
    }
  });
}
export const subscribeRoute = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
export const routeSnapshot = () => current;
export const setNavigationLock = (value: boolean): void => {
  locked = value;
};
export const navigate = (path: string, replace = false, focusId?: string): boolean => {
  const target = new URL(path, window.location.origin);
  if (target.origin !== window.location.origin) {
    return false;
  }
  if (target.pathname + target.search === current) {
    return true;
  }
  return guardedDeparture(() => {
    requestedFocus = focusId ?? null;
    window.history[replace ? "replaceState" : "pushState"](
      null,
      "",
      target.pathname + target.search,
    );
    publish();
  }, target.pathname + target.search);
};

/** A refused or accepted disclosure changes the selected actor without leaving its authored operation. */
export const replaceDisclosureRoute = (path: string): void => {
  const target = new URL(path, window.location.origin);
  if (target.origin === window.location.origin) {
    window.history.replaceState(null, "", target.pathname + target.search);
    publish();
  }
};
