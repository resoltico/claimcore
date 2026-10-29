import { useCallback, useEffect, useMemo, useSyncExternalStore } from "react";
import type { EndpointId, RequestByEndpoint } from "@contracts/client";
import { requestEndpoint, resultNotice } from "../api/service";
import { canonicalInput, typedResult } from "./endpointCache";
import { useEndpointCache } from "./EndpointProvider";
import { useCredential } from "../session/SessionContext";
export const useEndpoint = <K extends EndpointId>(
  endpoint: K,
  input: RequestByEndpoint[K],
  enabled = true,
) => {
  const cache = useEndpointCache();
  if (cache === null) {
    throw new Error("An endpoint provider is required.");
  }
  const credential = useCredential();
  const serialized = canonicalInput(input);
  const key = `${credential}\u001f${endpoint}:${serialized}`;
  const subscribe = useCallback(
    (listener: () => void) => cache.subscribe(key, listener),
    [cache, key],
  );
  const snapshot = useCallback(() => cache.snapshot(key), [cache, key]);
  const state = useSyncExternalStore(subscribe, snapshot, snapshot);
  const request = useCallback(
    (signal: AbortSignal) => requestEndpoint(endpoint, credential, input, signal),
    [endpoint, credential, input],
  );
  useEffect(() => {
    if (enabled && state.loading) {
      void cache.fetch(key, request);
    }
  }, [cache, key, request, state.loading, enabled]);
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
    loading: enabled && state.loading,
    message:
      result === null || (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED")
        ? null
        : resultNotice(result),
    reload,
  };
};
