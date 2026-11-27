import type { SessionTerminationScope } from "@contracts/client";
import { isObject } from "../../protocol/client";
import { productMetadata } from "../../protocol/productMetadata";

const storageKey = "claimcore.sessionTerminations.v1";
const capacity = 16;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const requestId = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
type Scope = Pick<SessionTerminationScope, "installationId" | "requesterKey"> & {
  serviceOrigin: string;
  target: string;
};
export type TerminationAttempt = Scope & { id: string };

const valid = (value: unknown): value is TerminationAttempt => {
  if (!isObject(value)) {
    return false;
  }
  const { serviceOrigin, installationId, requesterKey, target, id } = value;
  return (
    serviceOrigin === location.origin &&
    typeof installationId === "string" &&
    uuid.test(installationId) &&
    typeof requesterKey === "string" &&
    (uuid.test(requesterKey) || /^[0-9a-f]{64}$/u.test(requesterKey)) &&
    typeof target === "string" &&
    (target === "self" || uuid.test(target)) &&
    typeof id === "string" &&
    requestId.test(id)
  );
};

const readRecords = () => {
  const raw = sessionStorage.getItem(storageKey);
  if (raw === null) {
    return { valid: [], damaged: false } as const;
  }
  const parsed: unknown = JSON.parse(raw);
  if (!Array.isArray(parsed)) {
    throw new Error("Invalid termination recovery records.");
  }

  const admitted = parsed
    .filter(valid)
    .map(({ serviceOrigin, installationId, requesterKey, target, id }) => ({
      serviceOrigin,
      installationId,
      requesterKey,
      target,
      id,
    }));
  return { valid: admitted, damaged: admitted.length !== parsed.length };
};

const matches = (left: Scope, right: Scope) =>
  left.serviceOrigin === right.serviceOrigin &&
  left.installationId === right.installationId &&
  left.requesterKey === right.requesterKey &&
  left.target === right.target;

export const terminationScope = (scope: SessionTerminationScope, target: string): Scope => ({
  serviceOrigin: location.origin,
  installationId: scope.installationId,
  requesterKey: scope.requesterKey,
  target,
});

const issuedAt = (id: string) => Number.parseInt(id.slice(0, 8) + id.slice(9, 13), 16);

// UUID issuance is millisecond-aligned. Preserve a strict inequality at finer server precision.
const before = (instant: number, boundary: string) => {
  const milliseconds = Date.parse(boundary.replace(/(\.\d{3})\d+Z$/u, "$1Z"));
  return (
    instant < milliseconds || (instant === milliseconds && /\.\d{3}0*[1-9]\d*Z$/u.test(boundary))
  );
};

export const expiredAt = (id: string, observedAt: string) =>
  before(issuedAt(id) + productMetadata.catalog.limits.operationWindowMilliseconds, observedAt);

export const retiredAt = (id: string, cutoff: string | null) =>
  cutoff !== null && before(issuedAt(id), cutoff);

export const recoveryFor = (scope: Scope, cutoff: string | null) => {
  try {
    const all = readRecords();
    const attempts = all.valid.filter((item) => matches(item, scope)).reverse();
    return {
      kind: "available" as const,
      attempts,
      other: all.valid.some(
        (item) =>
          item.installationId !== scope.installationId ||
          (!matches(item, scope) && !retiredAt(item.id, cutoff)),
      ),
      damaged: all.damaged,
    };
  } catch {
    return { kind: "unavailable" as const };
  }
};

export const couldReclaimTermination = (scope: Scope, observedAt: string) => {
  try {
    return readRecords().valid.some(
      (item) => item.installationId === scope.installationId && expiredAt(item.id, observedAt),
    );
  } catch {
    return false;
  }
};

export class TerminationCapacityError extends Error {}

export const retainTermination = (attempt: TerminationAttempt, cutoff: string | null) => {
  if (!valid(attempt) || retiredAt(attempt.id, cutoff)) {
    throw new Error("Invalid termination recovery request.");
  }
  // The tab quota bounds the shared document. Only an installation's own cutoff retires its IDs.
  const remaining = readRecords().valid.filter(
    (item) =>
      item.installationId !== attempt.installationId ||
      (!retiredAt(item.id, cutoff) && (!matches(item, attempt) || item.id !== attempt.id)),
  );
  if (
    remaining.filter((item) => item.installationId === attempt.installationId).length >= capacity
  ) {
    throw new TerminationCapacityError();
  }
  const next = JSON.stringify([...remaining, attempt]);
  sessionStorage.setItem(storageKey, next);
  if (sessionStorage.getItem(storageKey) !== next) {
    throw new Error("Termination recovery publication could not be verified.");
  }
};

export const clearTermination = (attempt: TerminationAttempt) => {
  const remaining = readRecords().valid.filter(
    (item) => !matches(item, attempt) || item.id !== attempt.id,
  );
  if (remaining.length === 0) {
    sessionStorage.removeItem(storageKey);
  } else {
    sessionStorage.setItem(storageKey, JSON.stringify(remaining));
  }
};
