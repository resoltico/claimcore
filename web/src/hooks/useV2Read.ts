import { useCallback, useEffect, useRef, useState } from "react";
import { resultMessage, type ApiResult, type EndpointOutcome } from "../api/v2";

type ReadState<T> = { value: T | null; message: string | null; loading: boolean };

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
          ? { value: null, message: resultMessage(result), loading: false }
          : { value, message: null, loading: false },
      );
    });
    return () => controller.abort();
  }, [key, request, select]);
  return state;
};

export const useRetryablePage = <R extends EndpointOutcome, T>(
  request: (cursor: string | null, signal: AbortSignal) => Promise<ApiResult<R>>,
  select: (
    response: R,
  ) => { readonly items: ReadonlyArray<T>; readonly nextCursor: string | null } | null,
) => {
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const pending = useRef<{ cursor: string | null; task: Promise<void> } | null>(null);

  const load = useCallback(
    (next: string | null): Promise<void> => {
      if (pending.current?.cursor === next) return pending.current.task;
      controller.current?.abort();
      const current = new AbortController();
      controller.current = current;
      setLoading(true);
      setMessage(null);
      const task = (async () => {
        const result = await request(next, current.signal);
        if (current.signal.aborted) return;
        const page = result.kind === "outcome" ? select(result.value) : null;
        if (page === null) setMessage(resultMessage(result));
        else {
          setItems((previous) => (next === null ? [...page.items] : [...previous, ...page.items]));
          setCursor(page.nextCursor);
        }
        setLoading(false);
      })().finally(() => {
        if (controller.current === current) pending.current = null;
      });
      pending.current = { cursor: next, task };
      return task;
    },
    [request, select],
  );

  useEffect(() => {
    void Promise.resolve().then(() => load(null));
    return () => controller.current?.abort();
  }, [load]);

  return { items, cursor, message, loading, load };
};
