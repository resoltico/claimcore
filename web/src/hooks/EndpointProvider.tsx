import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { flushSync } from "react-dom";
import { EndpointCache, privateDisplayLifetimeMs } from "./endpointCache";
const browserAvailable = () => document.visibilityState === "visible" && window.navigator.onLine;

const Context = createContext<EndpointCache | null>(null);
export const EndpointProvider = ({
  children,
  scope = "test",
}: {
  children: ReactNode;
  scope?: string;
}) => {
  const { cache } = useMemo(() => ({ scope, cache: new EndpointCache(browserAvailable) }), [scope]);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let frame: number | undefined;
    let suspended = false;
    const stopWatching = () => {
      clearTimeout(timer);
      if (frame !== undefined) {
        cancelAnimationFrame(frame);
      }
    };
    const update = (active: boolean) => {
      stopWatching();
      flushSync(() => {
        cache.changeDisplay(active);
      });
    };
    function observe() {
      const active = !suspended && browserAvailable();
      update(active);
      if (active) {
        watchDeadline();
      }
    }
    function checkFrame() {
      // A foreground frame can run before a delayed timer. Invalidate before it paints.
      if (cache.admitsDisplay(cache.display())) {
        frame = requestAnimationFrame(checkFrame);
      } else {
        observe();
      }
    }
    function watchDeadline() {
      timer = setTimeout(observe, privateDisplayLifetimeMs);
      frame = requestAnimationFrame(checkFrame);
    }
    const suspend = () => {
      suspended = true;
      update(false);
    };
    const resume = () => {
      suspended = false;
      observe();
    };
    const reportOffline = () => {
      update(false);
    };
    // Reconcile changes between render and listener installation.
    const active = browserAvailable();
    if (active !== cache.admitsDisplay(cache.display())) {
      cache.changeDisplay(active);
    }
    if (active) {
      watchDeadline();
    }
    window.addEventListener("focus", observe);
    window.addEventListener("pageshow", resume);
    window.addEventListener("pagehide", suspend);
    window.addEventListener("offline", reportOffline);
    window.addEventListener("online", observe);
    document.addEventListener("visibilitychange", observe);
    return () => {
      stopWatching();
      window.removeEventListener("focus", observe);
      window.removeEventListener("pageshow", resume);
      window.removeEventListener("pagehide", suspend);
      window.removeEventListener("offline", reportOffline);
      window.removeEventListener("online", observe);
      document.removeEventListener("visibilitychange", observe);
    };
  }, [cache]);
  return <Context value={cache}>{children}</Context>;
};
export const useEndpointCache = () => useContext(Context);

export const useDisplayGeneration = () => {
  const cache = useEndpointCache();
  if (cache === null) {
    throw new Error("An endpoint provider is required.");
  }
  return useSyncExternalStore(cache.subscribeInvalidation, cache.display, cache.display);
};
