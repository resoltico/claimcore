import type { EndpointId, RequestByEndpoint } from "@contracts/client";
import type { ServiceResponse } from "../api/service";
import { requestEndpoint } from "../api/service";
import { useEndpointCache } from "./EndpointProvider";
import { canonicalInput, typedResult, type EndpointCache } from "./endpointCache";
import { useCredential } from "../session/SessionContext";
import type { Notice } from "../protocol/notices";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { resultNotice, type ApiResult, type EndpointOutcome } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";

type Page<T> = { readonly items: ReadonlyArray<T>; readonly nextCursor: string | null };

type PageSinks<T, P> = {
  onDisclosureRefused: ((notice?: Notice) => void) | undefined;
  setItems: Dispatch<SetStateAction<T[]>>;
  setCursor: Dispatch<SetStateAction<string | null>>;
  setMessage: Dispatch<SetStateAction<Notice | null>>;
  setLoading: Dispatch<SetStateAction<boolean>>;
  setPage: Dispatch<SetStateAction<P | null>>;
};

const fetchPage = async <R extends EndpointOutcome, T, P extends Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  sinks: PageSinks<T, P>,
  cursor: string | null,
  signal: AbortSignal,
): Promise<void> => {
  const result = await request(cursor, signal);
  if (signal.aborted) {
    return;
  }
  const page = result.kind === "outcome" ? select(result.value) : null;
  if (page === null) {
    const cursorInvalid =
      result.kind === "outcome" &&
      result.value.outcome.kind === "REJECTED" &&
      result.value.outcome.data.code === "INVALID_CURSOR";
    if (isDisclosureRefused(result) || cursorInvalid) {
      if (isDisclosureRefused(result)) {
        sinks.onDisclosureRefused?.(resultNotice(result));
      }
      sinks.setItems([]);
      sinks.setCursor(null);
      sinks.setPage(null);
    }
    sinks.setMessage(resultNotice(result));
  } else {
    sinks.setItems((previous) =>
      cursor === null ? [...page.items] : [...previous, ...page.items],
    );
    sinks.setCursor(page.nextCursor);
    sinks.setPage(page);
  }
  sinks.setLoading(false);
};

const usePageLoader = <R extends EndpointOutcome, T, P extends Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  sinks: PageSinks<T, P>,
) => {
  const controller = useRef<AbortController | null>(null);
  const pending = useRef<{ cursor: string | null; task: Promise<void> } | null>(null);
  const load = useCallback(
    (next: string | null): Promise<void> => {
      if (pending.current?.cursor === next) {
        return pending.current.task;
      }
      controller.current?.abort();
      const current = new AbortController();
      controller.current = current;
      if (next === null) {
        sinks.setItems([]);
        sinks.setCursor(null);
        sinks.setPage(null);
      }
      sinks.setLoading(true);
      sinks.setMessage(null);
      const task = fetchPage(request, select, sinks, next, current.signal).finally(() => {
        if (controller.current === current) {
          pending.current = null;
        }
      });
      pending.current = { cursor: next, task };
      return task;
    },
    [request, select, sinks],
  );
  const abort = useCallback(() => {
    controller.current?.abort();
    pending.current = null;
  }, []);
  return { load, abort };
};

const usePages = <R extends EndpointOutcome, T, P extends Page<T> = Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  onDisclosureRefused?: (notice?: Notice) => void,
) => {
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);
  const [loading, setLoading] = useState(false);
  const [page, setPage] = useState<P | null>(null);
  const sinks = useMemo(
    () => ({ setItems, setCursor, setMessage, setLoading, setPage, onDisclosureRefused }),
    [setItems, setCursor, setMessage, setLoading, setPage, onDisclosureRefused],
  );
  const { load, abort } = usePageLoader(request, select, sinks);

  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => {
      if (active) {
        void load(null);
      }
    });
    return () => {
      active = false;
      abort();
    };
  }, [abort, load]);

  return { items, cursor, message, loading, page, load };
};

type PageSource<K extends EndpointId> = {
  subscribe: EndpointCache["subscribe"];
  fetch: EndpointCache["fetch"];
  credential: string;
  endpoint: K;
  input: RequestByEndpoint[K];
  onResult: ((result: ApiResult<ServiceResponse<K>>) => void) | undefined;
};
const endpointPage = async <K extends EndpointId>(source: PageSource<K>, signal: AbortSignal) => {
  const key = `${source.credential}\u001f${source.endpoint}:${canonicalInput(source.input)}`;
  const release = source.subscribe(key, () => undefined);
  signal.addEventListener("abort", release, { once: true });
  try {
    const raw = await source.fetch(key, (owned) =>
      requestEndpoint(source.endpoint, source.credential, source.input, owned),
    );
    const result = typedResult(source.endpoint, raw);
    if (result === null) {
      throw new Error("A completed endpoint request requires a result.");
    }
    if (!signal.aborted) {
      source.onResult?.(result);
    }
    return result;
  } finally {
    signal.removeEventListener("abort", release);
    release();
  }
};
export const useEndpointPage = <K extends EndpointId, T, P extends Page<T>>(
  endpoint: K,
  input: (cursor: string | null) => RequestByEndpoint[K],
  select: (response: ServiceResponse<K>) => P | null,
  onDisclosureRefused?: (notice?: Notice) => void,
  onResult?: (result: ApiResult<ServiceResponse<K>>) => void,
) => {
  const cache = useEndpointCache();
  const credential = useCredential();
  if (cache === null) {
    throw new Error("An endpoint provider is required.");
  }
  const { subscribe, fetch: fetchEndpoint } = cache;
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      endpointPage(
        { subscribe, fetch: fetchEndpoint, credential, endpoint, input: input(cursor), onResult },
        signal,
      ),
    [subscribe, fetchEndpoint, credential, endpoint, input, onResult],
  );
  return usePages<ServiceResponse<K>, T, P>(request, select, onDisclosureRefused);
};
