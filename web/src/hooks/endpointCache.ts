import type { EndpointId, HumanDocument, Representation } from "@contracts/client";
import {
  linkedCaseReceipt,
  type ApiResult,
  type EndpointOutcome,
  type ServiceResponse,
} from "../protocol/types";
import { localNotice } from "../protocol/notices";
import { isDisclosureRefused } from "../api/outcomes";
export type CacheSnapshot = Readonly<{
  result: ApiResult<EndpointOutcome> | null;
  previous: EndpointOutcome | null;
  loading: boolean;
  version: number;
}>;
export const isReadBusy = (result: ApiResult<EndpointOutcome>): boolean =>
  result.kind === "hostFailure" && result.failure.code === "RATE_LIMITED";

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
type AttachmentInvalidation = { caseId: string; sequence: number | undefined };

const admittedRead = (reply: EndpointOutcome | null): HumanDocument | null => {
  if (reply === null || !("representation" in reply) || reply.representation !== "HUMAN") {
    return null;
  }
  const { outcome } = reply;
  return outcome.kind === "SUCCEEDED" && "document" in outcome ? outcome.document : null;
};

const retainsStaleAttachments = (
  previous: EndpointOutcome | null,
  change: AttachmentInvalidation,
) => {
  const basis = admittedRead(previous)?.attachmentObservation;
  return (
    basis?.caseId === change.caseId &&
    (change.sequence === undefined || basis.sequence < change.sequence)
  );
};

const isErasedCaseRead = (result: ApiResult<EndpointOutcome> | null, caseId: string) => {
  const document = admittedRead(result?.kind === "outcome" ? result.value : null);
  return (
    document?.subject.kind === "CASE" &&
    document.subject.caseId === caseId &&
    document.disclosure === "ERASED"
  );
};
const canonicalInput = (value: unknown): string => {
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
export function typedResult<K extends EndpointId>(
  endpoint: K,
  result: ApiResult<EndpointOutcome> | null,
): ApiResult<ServiceResponse<K>> | null;
export function typedResult<K extends EndpointId, R extends Representation>(
  endpoint: K,
  result: ApiResult<EndpointOutcome> | null,
  representation: R,
): ApiResult<ServiceResponse<K, R>> | null;
export function typedResult<K extends EndpointId>(
  _endpoint: K,
  result: ApiResult<EndpointOutcome> | null,
  representation: Representation = "HUMAN",
): ApiResult<ServiceResponse<K, Representation>> | null {
  if (result === null || result.kind !== "outcome") {
    return result;
  }
  if ("representation" in result.value && result.value.representation !== representation) {
    return { kind: "deliveryFailure", notice: localNotice("incomplete") };
  }
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Cache keys bind endpoint and representation; the server owns payload admission.
  return { ...result, value: result.value as ServiceResponse<K, Representation> };
}
export class EndpointCache {
  private readonly entries = new Map<string, Entry>();
  private generation = 0;
  private readonly refreshers = new Map<
    symbol,
    { screen: string; key: string | symbol; refresh: () => Promise<unknown> }
  >();
  private readonly lastRefresh = new Map<string, number>();
  private refreshInFlight: Promise<void> | null = null;

  registerRefresh = (screen: string, refresh: () => Promise<unknown>, key?: string) => {
    const registration = Symbol("screen refresh");
    this.refreshers.set(registration, { screen, refresh, key: key ?? registration });
    return () => {
      this.refreshers.delete(registration);
    };
  };

  revalidate = (screen: string, now: number, force = false): Promise<void> => {
    if (this.refreshInFlight !== null) {
      return this.refreshInFlight;
    }
    const last = this.lastRefresh.get(screen);
    if (!force && last !== undefined && now - last < 30_000) {
      return Promise.resolve();
    }
    this.lastRefresh.set(screen, now);
    const registrations = [...this.refreshers.keys()];
    const refreshed = new Set<string | symbol>();
    this.refreshInFlight = registrations
      .reduce(
        (previous, registration) =>
          previous.then(async () => {
            const refresher = this.refreshers.get(registration);
            if (refresher?.screen === screen && !refreshed.has(refresher.key)) {
              refreshed.add(refresher.key);
              await refresher.refresh();
            }
          }),
        Promise.resolve(),
      )
      .finally(() => {
        this.refreshInFlight = null;
      });
    return this.refreshInFlight;
  };
  private readonly attachmentDisclosures = new Map<string, number>();
  private readonly erasedAttachmentCases = new Set<string>();
  attachmentsErased = (caseId: string): boolean => this.erasedAttachmentCases.has(caseId);
  observeAttachmentErasure = (caseId: string): boolean => {
    if (this.attachmentsErased(caseId)) {
      return false;
    }
    this.erasedAttachmentCases.add(caseId);
    this.invalidateSnapshots({ caseId, sequence: undefined });
    return true;
  };
  observeCaseErasure = (document: HumanDocument): void => {
    if (document.subject.kind === "CASE" && document.disclosure === "ERASED") {
      this.observeAttachmentErasure(document.subject.caseId);
    }
  };
  permitsAttachmentReceipt = (receipt: HumanDocument): boolean => {
    const fields = receipt.attachmentObservation;
    return (
      receipt.subject.kind !== "RECEIPT" ||
      fields === undefined ||
      receipt.disclosure === "ERASED" ||
      !this.attachmentsErased(fields.caseId)
    );
  };
  observeAttachmentReceipt = (receipt: HumanDocument): boolean => {
    if (receipt.subject.kind !== "RECEIPT") {
      return true;
    }
    const fields = receipt.attachmentObservation;
    if (receipt.disclosure === "ERASED") {
      const caseId = fields?.caseId ?? linkedCaseReceipt(receipt)?.caseId;
      if (caseId !== undefined) {
        this.observeAttachmentErasure(caseId);
      }
      return true;
    }
    if (fields === undefined) {
      return true;
    }
    const { caseId, sequence } = fields;
    if (this.attachmentsErased(caseId)) {
      return false;
    }
    this.confirmAttachmentChange(caseId, sequence);
    return true;
  };
  attachmentDisclosure = (caseId: string): number | undefined =>
    this.attachmentDisclosures.get(caseId);
  private admitAttachmentSequence(caseId: string, sequence: number, confirmedChange: boolean) {
    if (this.attachmentsErased(caseId)) {
      return false;
    }
    const last = this.attachmentDisclosure(caseId);
    if (last === undefined || sequence > last) {
      this.attachmentDisclosures.set(caseId, sequence);
      if (last !== undefined || confirmedChange) {
        this.invalidateSnapshots({ caseId, sequence });
      }
      return true;
    }
    return sequence === last;
  }
  observeAttachmentDisclosure = (caseId: string, sequence: number): boolean =>
    this.admitAttachmentSequence(caseId, sequence, false);
  confirmAttachmentChange = (caseId: string, sequence: number): void => {
    this.admitAttachmentSequence(caseId, sequence, true);
  };
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
    quiet = false,
  ): Promise<ApiResult<EndpointOutcome>> => {
    const entry = this.entry(key);
    if (entry.pending !== null && !retry) {
      return entry.pending;
    }
    entry.request = request;
    entry.controller?.abort();
    const controller = new AbortController();
    entry.controller = controller;
    if (!quiet) {
      this.publish(entry, { result: null, previous: entry.snapshot.previous, loading: true });
    }
    const pending = request(controller.signal).then((result) => {
      if (entry.controller === controller && !controller.signal.aborted) {
        if (!quiet || !isReadBusy(result)) {
          this.publish(entry, {
            result,
            loading: false,
            previous: previousOutcome(result, entry.snapshot.previous),
          });
        }
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
    this.invalidateSnapshots();
  };
  private invalidateSnapshots(change?: AttachmentInvalidation): void {
    this.generation += 1;
    for (const entry of this.entries.values()) {
      if (
        change !== undefined &&
        this.attachmentsErased(change.caseId) &&
        isErasedCaseRead(entry.snapshot.result, change.caseId)
      ) {
        continue;
      }
      entry.controller?.abort();
      entry.pending = null;
      const { previous } = entry.snapshot;
      this.publish(entry, {
        result: null,
        previous:
          change !== undefined && retainsStaleAttachments(previous, change) ? null : previous,
        loading: true,
      });
    }
    this.invalidationListeners.forEach((listener) => {
      listener();
    });
  }
}
