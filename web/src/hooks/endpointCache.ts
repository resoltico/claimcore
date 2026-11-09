import type { EndpointId, Representation } from "@contracts/client";
import type { ApiResult, EndpointOutcome, ServiceResponse } from "../protocol/types";
import { isResponse } from "../protocol/client";
import { localNotice } from "../protocol/notices";
import { isDisclosureRefused } from "../api/outcomes";
export type CacheSnapshot = Readonly<{
  result: ApiResult<EndpointOutcome> | null;
  previous: EndpointOutcome | null;
  loading: boolean;
  version: number;
}>;
const empty: CacheSnapshot = { result: null, previous: null, loading: true, version: 0 };
type Entry = {
  snapshot: CacheSnapshot;
  listeners: Set<() => void>;
  controller: AbortController | null;
  pending: Promise<ApiResult<EndpointOutcome>> | null;
  request: ((signal: AbortSignal) => Promise<ApiResult<EndpointOutcome>>) | null;
};
const compareNames = (left: string, right: string): number => {
  if (left < right) {
    return -1;
  }
  return left > right ? 1 : 0;
};
const previousOutcome = (result: ApiResult<EndpointOutcome>, previous: EndpointOutcome | null) => {
  if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
    return result.value;
  }
  return isDisclosureRefused(result) ? null : previous;
};
export const canonicalInput = (value: unknown): string => {
  if (Array.isArray(value)) {
    return `[${value.map(canonicalInput).join(",")}]`;
  }
  if (typeof value === "object" && value !== null) {
    return `{${Object.entries(value)
      .sort(([left], [right]) => compareNames(left, right))
      .map(([key, item]) => `${JSON.stringify(key)}:${canonicalInput(item)}`)
      .join(",")}}`;
  }
  return JSON.stringify(value) ?? "null";
};
/** Transport observations differ by owner, binding and representation; business identity does not. */
export const endpointCacheKey = (
  scope: string,
  endpoint: EndpointId,
  input: unknown,
  representation: Representation,
  installationId?: string,
): string =>
  `${scope}\u001f${installationId ?? ""}\u001f${representation}\u001f${endpoint}:${canonicalInput(input)}`;
export const typedResult = <K extends EndpointId>(
  endpoint: K,
  result: ApiResult<EndpointOutcome> | null,
): ApiResult<ServiceResponse<K>> | null => {
  if (result === null || result.kind !== "outcome") {
    return result;
  }
  return isResponse(endpoint, result.value)
    ? { ...result, value: result.value }
    : { kind: "deliveryFailure", notice: localNotice("incomplete") };
};
export class EndpointCache {
  private readonly entries = new Map<string, Entry>();
  private generation = 0;
  private readonly invalidationListeners = new Set<() => void>();
  invalidation = (): number => this.generation;
  subscribeInvalidation = (listener: () => void) => {
    this.invalidationListeners.add(listener);
    return () => {
      this.invalidationListeners.delete(listener);
    };
  };
  private entry(key: string): Entry {
    let entry = this.entries.get(key);
    if (entry === undefined) {
      entry = {
        snapshot: empty,
        listeners: new Set(),
        controller: null,
        pending: null,
        request: null,
      };
      this.entries.set(key, entry);
    }
    return entry;
  }
  snapshot = (key: string): CacheSnapshot => this.entries.get(key)?.snapshot ?? empty;
  subscribe = (key: string, listener: () => void) => {
    const entry = this.entry(key);
    entry.listeners.add(listener);
    return () => {
      entry.listeners.delete(listener);
      if (entry.listeners.size === 0) {
        entry.controller?.abort();
        this.entries.delete(key);
      }
    };
  };
  private publish(entry: Entry, next: Omit<CacheSnapshot, "version">): void {
    entry.snapshot = { ...next, version: entry.snapshot.version + 1 };
    entry.listeners.forEach((listener) => {
      listener();
    });
  }
  fetch = (
    key: string,
    request: (signal: AbortSignal) => Promise<ApiResult<EndpointOutcome>>,
    retry = false,
  ): Promise<ApiResult<EndpointOutcome>> => {
    const entry = this.entry(key);
    if (entry.pending !== null && !retry) {
      return entry.pending;
    }
    entry.request = request;
    entry.controller?.abort();
    const controller = new AbortController();
    entry.controller = controller;
    this.publish(entry, { result: null, previous: entry.snapshot.previous, loading: true });
    const pending = request(controller.signal).then((result) => {
      if (entry.controller === controller && !controller.signal.aborted) {
        this.publish(entry, {
          result,
          loading: false,
          previous: previousOutcome(result, entry.snapshot.previous),
        });
        entry.pending = null;
      }
      return result;
    });
    entry.pending = pending;
    return pending;
  };
  retry = (key: string): void => {
    const request = this.entries.get(key)?.request;
    if (request) {
      void this.fetch(key, request, true);
    }
  };
  invalidate = (): void => {
    this.generation += 1;
    for (const entry of this.entries.values()) {
      entry.controller?.abort();
      entry.pending = null;
      this.publish(entry, { result: null, previous: entry.snapshot.previous, loading: true });
    }
    this.invalidationListeners.forEach((listener) => {
      listener();
    });
  };
}
