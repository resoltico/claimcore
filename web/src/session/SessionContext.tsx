import { createContext, useContext } from "react";
import type { useSession } from "./useSession";
export const SessionContext = createContext<ReturnType<typeof useSession> | null>(null);
export const useSessionContext = () => {
  const session = useContext(SessionContext);
  if (session === null) {
    throw new Error("A session provider is required.");
  }
  return session;
};
export const useCredential = (): string => {
  const { state } = useSessionContext();
  if (state.kind !== "authenticated") {
    return "";
  }
  return state.token;
};
