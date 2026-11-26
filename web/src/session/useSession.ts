import type { apiEndpoints } from "../api/service";
import {
  type ApiResult,
  resultNotice,
  type SessionSnapshot,
  service,
  type ServiceResponse,
} from "../api/service";
import { consumerContractIdentity } from "@contracts/client";
import { localNotice, type Notice } from "../protocol/notices";
import { markSignedOut, resetSignIn } from "../routing/signInAttempt";

import { useCallback, useEffect, useRef, useState } from "react";

export type SessionState =
  | { kind: "loading"; epoch: number }
  | { kind: "anonymous"; token: string | null; message: Notice | null; epoch: number }
  | { kind: "authenticated"; token: string; epoch: number }
  | { kind: "failure"; message: Notice; epoch: number };

type SessionResponse = ServiceResponse<typeof apiEndpoints.session>;

const snapshot = (result: ApiResult<SessionResponse>): SessionSnapshot | null =>
  result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED"
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
  const read = useRef<AbortController | null>(null);

  const refresh = useCallback(async (): Promise<void> => {
    const requestId = ++requestSerial.current;
    read.current?.abort();
    const controller = new AbortController();
    read.current = controller;
    const result = await service.session(controller.signal);
    if (requestSerial.current !== requestId) {
      return;
    }
    const value = snapshot(result);
    const next = stateFor(value, value === null ? resultNotice(result) : null, nextEpoch(epoch));
    if (next.kind === "authenticated") {
      resetSignIn(window.sessionStorage);
    }
    setState(next);
  }, []);

  useEffect(() => {
    void refresh();
    return () => {
      requestSerial.current += 1;
      read.current?.abort();
    };
  }, [refresh]);

  const logout = async (): Promise<void> => {
    if (state.kind !== "authenticated") {
      return;
    }
    const requestId = ++requestSerial.current;
    read.current?.abort();
    const result = await service.logout(state.token);
    if (requestSerial.current !== requestId) {
      return;
    }
    const data =
      result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED"
        ? result.value.outcome.data
        : null;
    const logoutEpoch = nextEpoch(epoch);
    if (data === null) {
      setState(stateFor(null, resultNotice(result), logoutEpoch));
      return;
    }
    markSignedOut(window.sessionStorage);
    const message =
      data.kind === "LOCAL_ONLY" && data.reason !== "NO_PROVIDER_SESSION"
        ? {
            kind: "diagnostic" as const,
            diagnostic: { code: data.reason, phase: "NOT_STARTED" as const },
          }
        : null;
    setState({ kind: "anonymous", token: null, message, epoch: logoutEpoch });
    if (data.kind === "PROVIDER") {
      window.location.replace(data.location);
    }
  };

  const endedBrowserSessions = (expectedEpoch: number): void => {
    if (epoch.current !== expectedEpoch) {
      return;
    }
    requestSerial.current += 1;
    read.current?.abort();
    markSignedOut(window.sessionStorage);
    setState(
      stateFor(
        {
          authenticated: false,
          antiforgeryToken: null,
          contractIdentity: consumerContractIdentity,
        },
        null,
        nextEpoch(epoch),
      ),
    );
  };
  return { state, logout, refresh, endedBrowserSessions };
};
