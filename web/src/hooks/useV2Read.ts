import type { Notice } from "../api/notices";
import { useCallback, useEffect, useRef, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { resultNotice, type ApiResult, type EndpointOutcome } from "../api/v2";

type ReadState<T> = { value: T | null; message: Notice | null; loading: boolean };
type Page<T> = { readonly items: ReadonlyArray<T>; readonly nextCursor: string | null };

export const useV2Read = <R extends EndpointOutcome, T>(
  request: (signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => T | null,
  key: string,
): ReadState<T> => {
  const [state, setState] = useState<ReadState<T>>({ value: null, message: null, loading: true });
  useEffect(() => {
    const controller = new AbortController();
    queueMicrotask(() => setState({ value: null, message: null, loading: true }));
    void request(controller.signal).then((result) => {
      if (controller.signal.aborted) return;
      const value = result.kind === "outcome" ? select(result.value) : null;
      setState(
        value === null
          ? { value: null, message: resultNotice(result), loading: false }
          : { value, message: null, loading: false },
      );
    });
    return () => controller.abort();
  }, [key, request, select]);
  return state;
};

const usePageLoader = <R extends EndpointOutcome, T, P extends Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
  setItems: Dispatch<SetStateAction<T[]>>,
  setCursor: Dispatch<SetStateAction<string | null>>,
  setMessage: Dispatch<SetStateAction<Notice | null>>,
  setLoading: Dispatch<SetStateAction<boolean>>,
  setPage: Dispatch<SetStateAction<P | null>>,
) => {
  const controller = useRef<AbortController | null>(null);
  const pending = useRef<{ cursor: string | null; task: Promise<void> } | null>(null);
  const load = useCallback(
    (next: string | null): Promise<void> => {
      if (pending.current?.cursor === next) return pending.current.task;
      controller.current?.abort();
      const current = new AbortController();
      controller.current = current;
      if (next === null) {
        setItems([]);
        setCursor(null);
        setPage(null);
      }
      setLoading(true);
      setMessage(null);
      const task = (async () => {
        const result = await request(next, current.signal);
        if (current.signal.aborted) return;
        const page = result.kind === "outcome" ? select(result.value) : null;
        if (page === null) setMessage(resultNotice(result));
        else {
          setItems((previous) => (next === null ? [...page.items] : [...previous, ...page.items]));
          setCursor(page.nextCursor);
          setPage(page);
        }
        setLoading(false);
      })().finally(() => {
        if (controller.current === current) pending.current = null;
      });
      pending.current = { cursor: next, task };
      return task;
    },
    [request, select, setCursor, setItems, setLoading, setMessage, setPage],
  );
  const abort = useCallback(() => controller.current?.abort(), []);
  return { load, abort };
};

export const useRetryablePage = <R extends EndpointOutcome, T, P extends Page<T> = Page<T>>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (response: R) => P | null,
) => {
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);
  const [loading, setLoading] = useState(false);
  const [currentPage, setPage] = useState<P | null>(null);
  const { load, abort } = usePageLoader(
    request,
    select,
    setItems,
    setCursor,
    setMessage,
    setLoading,
    setPage,
  );

  useEffect(() => {
    void Promise.resolve().then(() => load(null));
    return abort;
  }, [abort, load]);

  return { items, cursor, message, loading, page: currentPage, load };
};
