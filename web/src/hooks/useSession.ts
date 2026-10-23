import { consumerContractIdentity } from "@contracts/client";
import { localNotice } from "../api/notices";
import type { Notice } from "../api/notices";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  type ApiResult,
  resultNotice,
  type SessionSnapshot,
  service,
  type ServiceResponse,
} from "../api/service";

export type SessionState =
  | { kind: "loading"; epoch: number }
  | { kind: "anonymous"; token: string | null; message: Notice | null; epoch: number }
  | { kind: "authenticated"; token: string; epoch: number }
  | { kind: "failure"; message: Notice; epoch: number };

type SessionResponse = ServiceResponse<"session"> | ServiceResponse<"session.logout">;

const snapshot = (result: ApiResult<SessionResponse>): SessionSnapshot | null =>
  result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED"
    ? result.value.outcome.data
    : null;

const stateFor = (
  value: SessionSnapshot | null,
  failed: Notice | null,
  epoch: number,
): SessionState => {
  if (failed !== null || value === null) {
    return { kind: "failure", message: failed ?? localNotice("invalidSession"), epoch };
  }
  if (value.contractIdentity !== consumerContractIdentity) {
    return { kind: "failure", message: localNotice("definitionMismatch"), epoch };
  }
  if (!value.authenticated) {
    return { kind: "anonymous", token: value.antiforgeryToken, message: null, epoch };
  }
  return value.antiforgeryToken === null
    ? {
        kind: "failure",
        message: localNotice("missingCsrf"),
        epoch,
      }
    : { kind: "authenticated", token: value.antiforgeryToken, epoch };
};

const nextEpoch = (epoch: { current: number }): number => {
  epoch.current += 1;
  return epoch.current;
};

export const useSession = () => {
  const [state, setState] = useState<SessionState>({ kind: "loading", epoch: 0 });
  const epoch = useRef(0);
  const requestSerial = useRef(0);

  const refresh = useCallback(async (): Promise<void> => {
    const requestId = ++requestSerial.current;
    const result = await service.session();
    if (requestSerial.current !== requestId) {
      return;
    }
    const value = snapshot(result);
    setState(stateFor(value, value === null ? resultNotice(result) : null, nextEpoch(epoch)));
  }, []);

  useEffect(() => {
    void refresh();
    return () => {
      requestSerial.current += 1;
    };
  }, [refresh]);

  const logout = async (): Promise<void> => {
    if (state.kind !== "authenticated") {
      return;
    }
    const requestId = ++requestSerial.current;
    const result = await service.logout(state.token);
    if (requestSerial.current !== requestId) {
      return;
    }
    const value =
      result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED"
        ? {
            authenticated: false,
            antiforgeryToken: null,
            contractIdentity: consumerContractIdentity,
          }
        : null;
    // Only successful unit replies confirm logout; every result advances the privacy epoch.
    setState(stateFor(value, value === null ? resultNotice(result) : null, nextEpoch(epoch)));
  };

  return { state, logout, refresh };
};
