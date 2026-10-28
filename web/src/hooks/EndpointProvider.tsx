import { createContext, useContext, useMemo, type ReactNode } from "react";
import { EndpointCache } from "./endpointCache";
const Context = createContext<EndpointCache | null>(null);
export const EndpointProvider = ({ children }: { children: ReactNode }) => {
  const cache = useMemo(() => new EndpointCache(), []);
  return <Context value={cache}>{children}</Context>;
};
export const useEndpointCache = () => useContext(Context);
