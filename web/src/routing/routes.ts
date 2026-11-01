import type { CaseSelector } from "@contracts/client";
import { isRequest } from "../protocol/client";
export type Route =
  | { screen: "cases"; query: string }
  | { screen: "newCase" }
  | { screen: "case"; target: CaseSelector }
  | { screen: "caseAct"; target: CaseSelector; act: string }
  | { screen: "operations"; operationId: string }
  | { screen: "authority"; actorId: string; history: boolean }
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

const caseRoute = (segments: string[]): Route => {
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
  return segments[3] === "acts"
    ? { screen: "caseAct", target, act: decodeURIComponent(segments[4]!) }
    : { screen: "missing" };
};
export const parseRoute = (location: Pick<Location, "pathname" | "search">): Route => {
  const segments = location.pathname.split("/").filter(Boolean);
  try {
    if (segments.length === 0 || (segments[0] === "cases" && segments.length === 1)) {
      return { screen: "cases", query: location.search };
    }
    if (segments[0] === "cases") {
      return caseRoute(segments);
    }
    if (segments[0] === "operations" && segments.length <= 2) {
      return { screen: "operations", operationId: decodeURIComponent(segments[1] ?? "") };
    }
    if (segments[0] === "authority" && segments.length <= 2) {
      return {
        screen: "authority",
        actorId: decodeURIComponent(segments[1] ?? ""),
        history: new URLSearchParams(location.search).get("tab") === "history",
      };
    }
  } catch {
    return { screen: "missing" };
  }
  return { screen: "missing" };
};
