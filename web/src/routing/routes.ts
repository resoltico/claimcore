import type { CaseSelector } from "@contracts/client";
import { isRequest } from "../protocol/client";
export type Route =
  | { screen: "cases"; query: string }
  | { screen: "home" }
  | { screen: "reminders"; query: string }
  | { screen: "settings" }
  | { screen: "newCase" }
  | { screen: "case"; target: CaseSelector }
  | { screen: "caseAct"; target: CaseSelector; act: string; paymentId?: string }
  | { screen: "recovery"; operationId: string }
  | { screen: "people"; actorId: string }
  | { screen: "authorityHistory"; actorId: string }
  | { screen: "missing" };

const opaque = (value: string): string =>
  btoa(Array.from(new TextEncoder().encode(value), (byte) => String.fromCharCode(byte)).join(""))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/u, "");

const referenceSegment = (reference: string): string =>
  [".", ".."].includes(reference) || reference.startsWith("~")
    ? `~${opaque(reference)}`
    : encodeURIComponent(reference);

const referenceFrom = (segment: string): string => {
  if (!segment.startsWith("~")) {
    return decodeURIComponent(segment);
  }
  const bytes = atob(segment.slice(1).replaceAll("-", "+").replaceAll("_", "/"));
  const reference = new TextDecoder("utf-8", { fatal: true }).decode(
    Uint8Array.from(bytes, (value) => value.charCodeAt(0)),
  );
  if (referenceSegment(reference) !== segment) {
    throw new Error("Noncanonical reference path.");
  }
  return reference;
};

export const casePath = (caseId: string): string => `/cases/id/${encodeURIComponent(caseId)}`;
export const referencePath = (reference: string): string =>
  `/cases/reference/${referenceSegment(reference)}`;
export const actPath = (target: CaseSelector, act: string): string =>
  `${target.kind === "ID" ? casePath(target.caseId) : referencePath(target.caseReference)}/acts/${encodeURIComponent(act)}`;

const caseActRoute = (target: CaseSelector, segment: string, query: string): Route => {
  const act = decodeURIComponent(segment);
  const paymentIds = new URLSearchParams(query).getAll("paymentId");
  if (act === "REMOVE_PAYMENT" && (paymentIds.length !== 1 || paymentIds[0] === "")) {
    return { screen: "missing" };
  }
  return {
    screen: "caseAct",
    target,
    act,
    ...(act === "REMOVE_PAYMENT" ? { paymentId: paymentIds[0]! } : {}),
  };
};
const caseRoute = (segments: string[], query: string): Route => {
  if (segments.length === 2 && segments[1] === "new") {
    return { screen: "newCase" };
  }
  if (segments.length !== 3 && segments.length !== 5) {
    return { screen: "missing" };
  }
  const target =
    segments[1] === "id"
      ? { kind: "ID" as const, caseId: decodeURIComponent(segments[2]!) }
      : { kind: "REFERENCE" as const, caseReference: referenceFrom(segments[2]!) };
  if (!["id", "reference"].includes(segments[1]!) || !isRequest("case.get", { target })) {
    return { screen: "missing" };
  }
  if (segments.length === 3) {
    return { screen: "case", target };
  }
  if (segments[3] !== "acts") {
    return { screen: "missing" };
  }
  return caseActRoute(target, segments[4]!, query);
};
const singleRoute = (segments: ReadonlyArray<string>, query: string): Route | null => {
  if (segments.length === 0) {
    return { screen: "home" };
  }
  if (segments.length !== 1) {
    return null;
  }
  if (segments[0] === "settings") {
    return { screen: "settings" };
  }
  if (segments[0] === "cases") {
    return { screen: "cases", query };
  }
  if (segments[0] === "authority-history") {
    const actors = new URLSearchParams(query).getAll("actorId");
    const actorId = actors[0] ?? "";
    if (
      actors.length > 1 ||
      (actors.length === 1 && !isRequest("authority.history", { limit: 50, actorId }))
    ) {
      return { screen: "missing" };
    }
    return { screen: "authorityHistory", actorId };
  }
  return segments[0] === "reminders" ? { screen: "reminders", query } : null;
};
export const parseRoute = (location: Pick<Location, "pathname" | "search">): Route => {
  const segments = location.pathname.split("/").filter(Boolean);
  try {
    const simple = singleRoute(segments, location.search);
    if (simple !== null) {
      return simple;
    }
    if (segments[0] === "cases") {
      return caseRoute(segments, location.search);
    }
    if (segments[0] === "recovery" && segments.length <= 2) {
      return { screen: "recovery", operationId: decodeURIComponent(segments[1] ?? "") };
    }
    if (segments[0] === "people" && segments.length <= 2) {
      return {
        screen: "people",
        actorId: decodeURIComponent(segments[1] ?? ""),
      };
    }
  } catch {
    return { screen: "missing" };
  }
  return { screen: "missing" };
};
