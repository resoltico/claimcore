export type Route =
  | { screen: "cases"; query: string }
  | { screen: "newCase" }
  | { screen: "case"; reference: string }
  | { screen: "caseAct"; reference: string; act: string }
  | { screen: "operations"; operationId: string }
  | { screen: "authority"; actorId: string }
  | { screen: "missing" };

const opaque = (value: string): string =>
  btoa(Array.from(new TextEncoder().encode(value), (byte) => String.fromCharCode(byte)).join(""))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/u, "");

const referenceSegment = (reference: string): string =>
  ["new", ".", ".."].includes(reference) || reference.startsWith("~")
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

export const casePath = (reference: string): string => `/cases/${referenceSegment(reference)}`;
export const actPath = (reference: string, act: string): string =>
  `${casePath(reference)}/acts/${encodeURIComponent(act)}`;

const caseRoute = (segments: string[]): Route => {
  if (segments.length === 1) {
    return { screen: "cases", query: "" };
  }
  if (segments.length === 2) {
    return segments[1] === "new"
      ? { screen: "newCase" }
      : { screen: "case", reference: referenceFrom(segments[1]!) };
  }
  if (segments[2] === "acts" && segments.length === 4) {
    return {
      screen: "caseAct",
      reference: referenceFrom(segments[1]!),
      act: decodeURIComponent(segments[3]!),
    };
  }
  return { screen: "missing" };
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
      return { screen: "authority", actorId: decodeURIComponent(segments[1] ?? "") };
    }
  } catch {
    return { screen: "missing" };
  }
  return { screen: "missing" };
};
