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
    const refresh = () => {
      if (document.visibilityState === "visible") {
        void cache.revalidate(window.location.pathname, performance.now());
      }
    };
    window.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      window.removeEventListener("focus", refresh);
      document.removeEventListener("visibilitychange", refresh);
    };
  }, [cache]);
  return <Context value={cache}>{children}</Context>;
};
export const useEndpointCache = () => useContext(Context);
