import { localNotice } from "../api/notices";
import type { Notice } from "../api/notices";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  type ApiResult,
  resultNotice,
  type SessionSnapshot,
  v2,
  type WebV2Response,
} from "../api/v2";

export type SessionState =
  | { kind: "loading"; epoch: number }
  | { kind: "anonymous"; token: string | null; message: Notice | null; epoch: number }
  | { kind: "authenticated"; token: string; epoch: number }
  | { kind: "failure"; message: Notice; epoch: number };

type SessionResponse =
  WebV2Response<"session"> | WebV2Response<"session.login"> | WebV2Response<"session.logout">;

const snapshot = (result: ApiResult<SessionResponse>): SessionSnapshot | null =>
  result.kind === "outcome" && result.value.outcome.tag === "SNAPSHOT"
    ? result.value.outcome.data
    : null;

const stateFor = (
  value: SessionSnapshot | null,
  failed: Notice | null,
  epoch: number,
): SessionState => {
  if (failed !== null || value === null)
    return { kind: "failure", message: failed ?? localNotice("invalidSession"), epoch };
  if (!value.authenticated)
    return { kind: "anonymous", token: value.antiforgeryToken, message: null, epoch };
  return value.antiforgeryToken === null
    ? {
        kind: "failure",
        message: localNotice("missingCsrf"),
        epoch,
      }
    : { kind: "authenticated", token: value.antiforgeryToken, epoch };
};

export const useSession = () => {
  const [state, setState] = useState<SessionState>({ kind: "loading", epoch: 0 });
  const epoch = useRef(0);
  const nextEpoch = (): number => {
    epoch.current += 1;
    return epoch.current;
  };

  const refresh = useCallback(async (): Promise<void> => {
    const result = await v2.session();
    const value = snapshot(result);
    setState(stateFor(value, value === null ? resultNotice(result) : null, nextEpoch()));
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const login = async (credential: string): Promise<void> => {
    if (state.kind !== "anonymous" || state.token === null) return;
    const result = await v2.login(credential, state.token);
    if (result.kind === "hostFailure" && result.failure.code === "WEB_LOGIN_REJECTED") {
      setState({
        kind: "anonymous",
        token: state.token,
        message: resultNotice(result),
        epoch: nextEpoch(),
      });
      return;
    }
    const value = snapshot(result);
    setState(stateFor(value, value === null ? resultNotice(result) : null, nextEpoch()));
  };

  const logout = async (): Promise<void> => {
    if (state.kind !== "authenticated") return;
    const result = await v2.logout(state.token);
    const value = snapshot(result);
    // A successful logout response is an anonymous snapshot. No claimant-bearing state survives its epoch.
    setState(stateFor(value, value === null ? resultNotice(result) : null, nextEpoch()));
  };

  return { state, login, logout, refresh };
};
