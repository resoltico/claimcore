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
const read = (): UnresolvedOperation | null => {
  try {
    const raw = sessionStorage.getItem(key);
    const value: unknown = raw === null ? null : JSON.parse(raw);
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
export const unresolvedOperation = {
  read,
  save: (value: UnresolvedOperation): void => {
    try {
      sessionStorage.setItem(key, JSON.stringify(value));
    } catch {
      /* Optional reload notice. */
    }
  },
  clear: (operationId: string): void => {
    try {
      if (read()?.operationId === operationId) {
        sessionStorage.removeItem(key);
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
