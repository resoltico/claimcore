import type { WorkDateBasis, Screen, EndpointId, RequestByEndpoint } from "@contracts/client";
import type { PresentedPage, ServiceResponse } from "../api/service";
import { requestEndpoint } from "../api/service";
import { useEndpointCache } from "./EndpointProvider";
import { endpointCacheKey, typedResult, type EndpointCache } from "./endpointCache";
import { useCredential, useSessionContext } from "../session/SessionContext";
import type { Notice } from "../protocol/notices";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import type { Dispatch, SetStateAction } from "react";
import { resultNotice, type ApiResult, type EndpointOutcome } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";

type Page<T> = {
  readonly items: ReadonlyArray<T>;
  readonly nextCursor: string | null;
  readonly basis: WorkDateBasis;
  readonly screen: Screen;
};

type PageSinks<T, P> = {
  onDisclosureChanged: ((notice?: Notice) => void) | undefined;
  setItems: Dispatch<SetStateAction<T[]>>;
  setCursor: Dispatch<SetStateAction<string | null>>;
  setMessage: Dispatch<SetStateAction<Notice | null>>;
  setLoading: Dispatch<SetStateAction<boolean>>;
  setPage: Dispatch<SetStateAction<P | null>>;
  setViews: Dispatch<SetStateAction<PresentedPage[]>>;
};

const acceptPage = <T, P extends Page<T>>(
  page: P,
  sinks: PageSinks<T, P>,
  cursor: string | null,
  previousPage: { current: P | null },
  continuationKey?: (page: P) => string,
) => {
  const changed =
    cursor !== null &&
    previousPage.current !== null &&
    continuationKey !== undefined &&
    continuationKey(previousPage.current) !== continuationKey(page);
  const replace = cursor === null || changed;
  previousPage.current = page;
  sinks.setItems((previous) => (replace ? [...page.items] : [...previous, ...page.items]));
  sinks.setCursor(page.nextCursor);
  sinks.setPage(page);
  const presented = { view: page.screen, basis: page.basis };
  sinks.setViews((previous) => (replace ? [presented] : [...previous, presented]));
  if (changed) {
    sinks.onDisclosureChanged?.();
  }
};

const fetchPage = async <R extends EndpointOutcome, T, P extends Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  sinks: PageSinks<T, P>,
  cursor: string | null,
  signal: AbortSignal,
  previousPage: { current: P | null },
  continuationKey: ((page: P) => string) | undefined,
  current: () => boolean,
  publish: () => void,
): Promise<void> => {
  const result = await request(cursor, signal);
  if (signal.aborted || !current()) {
    return;
  }
  publish();
  const page = result.kind === "outcome" ? select(result.value) : null;
  if (page === null) {
    const rejected = result.kind === "outcome" && result.value.outcome.kind === "REJECTED";
    if (isDisclosureRefused(result) || rejected) {
      if (isDisclosureRefused(result)) {
        sinks.onDisclosureChanged?.(resultNotice(result));
      }
      previousPage.current = null;
      sinks.setItems([]);
      sinks.setCursor(null);
      sinks.setPage(null);
      sinks.setViews([]);
    }
    sinks.setMessage(resultNotice(result));
  } else {
    acceptPage(page, sinks, cursor, previousPage, continuationKey);
  }
  sinks.setLoading(false);
};

const usePageLoader = <R extends EndpointOutcome, T, P extends Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  sinks: PageSinks<T, P>,
  continuationKey: ((page: P) => string) | undefined,
  current: () => boolean,
  publish: () => void,
) => {
  const previousPage = useRef<P | null>(null);
  const controller = useRef<AbortController | null>(null);
  const pending = useRef<{ cursor: string | null; task: Promise<void> } | null>(null);
  const load = useCallback(
    (next: string | null): Promise<void> => {
      if (pending.current?.cursor === next) {
        return pending.current.task;
      }
      controller.current?.abort();
      const attempt = new AbortController();
      controller.current = attempt;
      if (next === null) {
        previousPage.current = null;
        sinks.setItems([]);
        sinks.setCursor(null);
        sinks.setPage(null);
        sinks.setViews([]);
      }
      sinks.setLoading(true);
      sinks.setMessage(null);
      const task = fetchPage(
        request,
        select,
        sinks,
        next,
        attempt.signal,
        previousPage,
        continuationKey,
        current,
        publish,
      ).finally(() => {
        if (controller.current === attempt) {
          pending.current = null;
        }
      });
      pending.current = { cursor: next, task };
      return task;
    },
    [request, select, sinks, continuationKey, current, publish],
  );
  const abort = useCallback(() => {
    controller.current?.abort();
    pending.current = null;
  }, []);
  return { load, abort };
};

const usePages = <R extends EndpointOutcome, T, P extends Page<T> = Page<T>>(
  owner: string,
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  onDisclosureChanged?: (notice?: Notice) => void,
  continuationKey?: (page: P) => string,
) => {
  const activeOwner = useRef(owner);
  useLayoutEffect(() => {
    activeOwner.current = owner;
  }, [owner]);
  const [publishedOwner, setPublishedOwner] = useState(owner);
  const current = useCallback(() => activeOwner.current === owner, [owner]);
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);
  const [loading, setLoading] = useState(false);
  const [page, setPage] = useState<P | null>(null);
  const [views, setViews] = useState<PresentedPage[]>([]);
  const sinks = useMemo(
    () => ({ setItems, setCursor, setMessage, setLoading, setPage, setViews, onDisclosureChanged }),
    [setItems, setCursor, setMessage, setLoading, setPage, setViews, onDisclosureChanged],
  );
  const publish = useCallback(() => {
    if (current()) {
      setPublishedOwner(owner);
    }
  }, [current, owner]);
  const { load, abort } = usePageLoader(request, select, sinks, continuationKey, current, publish);

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

  const visible = publishedOwner === owner;
  return {
    items: visible ? items : [],
    cursor: visible ? cursor : null,
    message: visible ? message : null,
    loading: !visible || loading,
    page: visible ? page : null,
    views: visible ? views : [],
    load,
  };
};

type PageSource<K extends EndpointId> = {
  subscribe: EndpointCache["subscribe"];
  fetch: EndpointCache["fetch"];
  credential: string;
  owner: string;
  generation: number;
  endpoint: K;
  input: RequestByEndpoint[K];
  onResult: ((result: ApiResult<ServiceResponse<K>>) => void) | undefined;
};
const endpointPage = async <K extends EndpointId>(source: PageSource<K>, signal: AbortSignal) => {
  const key = endpointCacheKey(
    `${source.owner}\u001f${source.generation}`,
    source.endpoint,
    source.input,
    "HUMAN",
  );
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
  onDisclosureChanged?: (notice?: Notice) => void,
  onResult?: (result: ApiResult<ServiceResponse<K>>) => void,
  continuationKey?: (page: P) => string,
) => {
  const cache = useEndpointCache();
  const credential = useCredential();
  const { state: session } = useSessionContext();
  const owner = `${location.origin}\u001f${session.epoch}:${credential}`;
  if (cache === null) {
    throw new Error("An endpoint provider is required.");
  }
  const generation = useSyncExternalStore(
    cache.subscribeInvalidation,
    cache.invalidation,
    cache.invalidation,
  );
  const { subscribe, fetch: fetchEndpoint } = cache;
  const identity = endpointCacheKey(`${owner}\u001f${generation}`, endpoint, input(null), "HUMAN");
  const activeIdentity = useRef(identity);
  useLayoutEffect(() => {
    activeIdentity.current = identity;
  }, [identity]);
  const observed = useCallback(
    (result: ApiResult<ServiceResponse<K>>) => {
      if (activeIdentity.current === identity) {
        onResult?.(result);
      }
    },
    [identity, onResult],
  );

  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      endpointPage(
        {
          subscribe,
          fetch: fetchEndpoint,
          credential,
          owner,
          endpoint,
          input: input(cursor),
          onResult: observed,
          generation,
        },
        signal,
      ),
    [subscribe, fetchEndpoint, credential, owner, endpoint, input, observed, generation],
  );
  return usePages<ServiceResponse<K>, T, P>(
    identity,
    request,
    select,
    onDisclosureChanged,
    continuationKey,
  );
};
