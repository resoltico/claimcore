import { createContext, useContext, useEffect, useMemo, type ReactNode } from "react";
import { EndpointCache } from "./endpointCache";
const Context = createContext<EndpointCache | null>(null);
export const EndpointProvider = ({
  children,
  scope = "test",
}: {
  children: ReactNode;
  scope?: string;
}) => {
  const { cache } = useMemo(() => ({ scope, cache: new EndpointCache() }), [scope]);
  useEffect(() => {
    let frame: number | null = null;
    const refresh = () => {
      if (document.visibilityState === "visible" && frame === null) {
        // Keep the first activation input ahead of a refresh that may withdraw active controls.
        frame = requestAnimationFrame(() => {
          frame = requestAnimationFrame(() => {
            frame = null;
            if (document.visibilityState === "visible") {
              void cache.revalidate(window.location.pathname, performance.now());
            }
          });
        });
      }
    };
    window.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      if (frame !== null) {
        cancelAnimationFrame(frame);
      }
      window.removeEventListener("focus", refresh);
      document.removeEventListener("visibilitychange", refresh);
    };
  }, [cache]);
  return <Context value={cache}>{children}</Context>;
};
export const useEndpointCache = () => useContext(Context);
