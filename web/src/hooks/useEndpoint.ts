import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useSyncExternalStore,
} from "react";
import type { EndpointId, RequestByEndpoint } from "@contracts/client";
import {
  requestEndpoint,
  resultNotice,
  type ApiResult,
  type EndpointOutcome,
} from "../api/service";
import { endpointCacheKey, typedResult } from "./endpointCache";
import { useEndpointCache } from "./EndpointProvider";
import { useCredential, useSessionContext } from "../session/SessionContext";
const readMessage = (result: ApiResult<EndpointOutcome> | null) =>
  result === null ||
  (result.kind === "outcome" && ["SUCCEEDED", "ACCEPTED"].includes(result.value.outcome.kind))
    ? null
    : resultNotice(result);
export const useEndpoint = <K extends EndpointId>(
  endpoint: K,
  input: RequestByEndpoint[K],
  enabled = true,
  scope = "",
  installationId?: string,
) => {
  const cache = useEndpointCache();
  if (cache === null) {
    throw new Error("An endpoint provider is required.");
  }
  const credential = useCredential();
  const { state: session } = useSessionContext();
  const active = enabled && session.kind === "authenticated";
  const key = endpointCacheKey(
    `${location.origin}\u001f${session.epoch}:${credential}\u001f${scope}`,
    endpoint,
    input,
    "HUMAN",
    installationId,
  );
  const subscribe = useCallback(
    (listener: () => void) => cache.subscribe(key, listener),
    [cache, key],
  );
  const snapshot = useCallback(() => cache.snapshot(key), [cache, key]);
  const state = useSyncExternalStore(subscribe, snapshot, snapshot);
  const display = cache.display();
  const request = useCallback(
    (signal: AbortSignal) => requestEndpoint(endpoint, credential, input, signal, installationId),
    [endpoint, credential, input, installationId],
  );
  useEffect(() => {
    if (active && state.loading && cache.admitsDisplay(display)) {
      void cache.fetch(key, request);
    }
  }, [active, cache, display, key, request, state.loading]);
  const screen = window.location.pathname;
  const refreshRequest = useRef(request);
  useLayoutEffect(() => {
    refreshRequest.current = request;
  }, [request]);
  useLayoutEffect(() => {
    if (!active) {
      return;
    }
    return cache.registerRefresh(
      screen,
      () => cache.fetch(key, refreshRequest.current, false, true),
      key,
    );
  }, [cache, key, active, screen]);
  const result = useMemo(() => typedResult(endpoint, state.result), [endpoint, state.result]);
  const previous = useMemo(
    () =>
      state.previous === null
        ? null
        : typedResult(endpoint, { kind: "outcome", status: 200, value: state.previous }),
    [endpoint, state.previous],
  );
  const reload = useCallback(() => {
    cache.retry(key);
  }, [cache, key]);
  return {
    result,
    value: result?.kind === "outcome" ? result.value : null,
    previous: previous?.kind === "outcome" ? previous.value : null,
    loading: active && state.loading,
    message: readMessage(result),
    reload,
  };
};
