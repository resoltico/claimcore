import { EndpointProvider } from "../hooks/EndpointProvider";
import { useEffect, type ReactNode } from "react";
import { useSession } from "./useSession";
import { SessionContext } from "./SessionContext";
import { restoreLoginReturn } from "../routing/loginReturn";
export const SessionProvider = ({ children }: { children: ReactNode }) => {
  const session = useSession();
  const authenticated = session.state.kind === "authenticated";
  useEffect(() => {
    if (authenticated) {
      restoreLoginReturn();
    }
  }, [authenticated]);
  return (
    <SessionContext value={session}>
      <EndpointProvider key={session.state.epoch}>{children}</EndpointProvider>
    </SessionContext>
  );
};
