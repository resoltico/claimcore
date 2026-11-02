import { createContext, useContext, useMemo, type ReactNode } from "react";
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
  return <Context value={cache}>{children}</Context>;
};
export const useEndpointCache = () => useContext(Context);
