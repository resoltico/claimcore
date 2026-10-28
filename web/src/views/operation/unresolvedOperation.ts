import { useEffect, useRef } from "react";
import type { OperationModel } from "../../hooks/operation/useOperation";
export type UnresolvedOperation = {
  operationId: string;
  subject: "CASE" | "AUTHORITY";
  act: string;
};
const key = "claimcore.unresolvedOperation";
const isSubject = (value: unknown): value is "CASE" | "AUTHORITY" =>
  value === "CASE" || value === "AUTHORITY";
const listeners = new Set<() => void>();
const snapshot = (): string | null => {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
};
const publish = (): void => {
  listeners.forEach((listener) => {
    listener();
  });
};
const parse = (raw: string): UnresolvedOperation | null => {
  try {
    const value: unknown = JSON.parse(raw);
    if (
      value !== null &&
      typeof value === "object" &&
      Object.keys(value).length === 3 &&
      "operationId" in value &&
      typeof value.operationId === "string" &&
      "subject" in value &&
      isSubject(value.subject) &&
      "act" in value &&
      typeof value.act === "string"
    ) {
      return { operationId: value.operationId, subject: value.subject, act: value.act };
    }
  } catch {
    /* Storage may be unavailable; the in-page operation still works. */
  }
  return null;
};
const read = (raw = snapshot()): UnresolvedOperation | null => (raw === null ? null : parse(raw));
export const unresolvedOperation = {
  read,
  snapshot,
  subscribe: (listener: () => void) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
  save: (value: UnresolvedOperation): void => {
    try {
      sessionStorage.setItem(key, JSON.stringify(value));
      publish();
    } catch {
      /* Optional reload notice. */
    }
  },
  clear: (operationId: string): void => {
    try {
      if (read()?.operationId === operationId) {
        sessionStorage.removeItem(key);
        publish();
      }
    } catch {
      /* Optional reload notice. */
    }
  },
};

/** Storage belongs to the browser view; request coordination stays platform-independent. */
export const useUnresolvedOperation = ({ phase, prepared, notice }: OperationModel): void => {
  const unresolvedId = useRef<string | null>(null);
  useEffect(() => {
    if (phase === "UNRESOLVED" && prepared !== null) {
      const { input } = prepared;
      unresolvedId.current = input.operationId;
      unresolvedOperation.save({
        operationId: input.operationId,
        subject: input.kind,
        act: input.act.kind,
      });
    } else if (
      phase === "ACCEPTED" ||
      (phase === "EDITING" && notice?.kind === "local" && notice.reason === "notAccepted")
    ) {
      const operationId = prepared?.input.operationId ?? unresolvedId.current;
      if (operationId !== null) {
        unresolvedOperation.clear(operationId);
      }
    }
  }, [phase, prepared, notice]);
};
