import { it, expect } from "vitest";
import { endpoints } from "@contracts/client";
import { isResponse, isObject } from "../src/protocol/client";
import { corpusCases, generatedWebValue } from "./contract-corpus.fixtures";

const ordinal = (left: string, right: string): number => {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
};

it("all positive native response families supply the consumed browser shape", () => {
  const reached = new Set<string>();
  for (const sample of corpusCases().filter(
    (candidate) => candidate.direction === "response" && candidate.valid,
  )) {
    expect(isResponse(sample.endpoint, sample.value), sample.id).toBe(true);
    reached.add(sample.endpoint);
    const incomplete: unknown = structuredClone(sample.value);
    if (!isObject(incomplete) || !isObject(incomplete["outcome"])) {
      throw new Error("Native response fixture absent.");
    }
    delete incomplete["outcome"]["data"];
    expect(isResponse(sample.endpoint, incomplete), sample.id).toBe(false);
  }
  expect([...reached].sort(ordinal)).toEqual(
    endpoints.map((endpoint) => endpoint.id).sort(ordinal),
  );
});

it("mutation guards require usable receipts and exact endpoint scope rather than only tags", () => {
  const accepted = generatedWebValue("case.execute");
  if (accepted.outcome.tag !== "ACCEPTED") {
    throw new Error("Case receipt fixture absent.");
  }
  for (const data of [
    null,
    {},
    { operationId: accepted.outcome.data.operationId },
    { ...accepted.outcome.data, position: null },
    { ...accepted.outcome.data, snapshot: { tag: "DISCLOSED" } },
    { ...accepted.outcome.data, revision: Number.MAX_SAFE_INTEGER + 1 },
    { ...accepted.outcome.data, scope: "AUTHORITY" },
  ]) {
    expect(
      isResponse("case.execute", {
        endpoint: "case.execute",
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { tag: "ACCEPTED", data },
      }),
    ).toBe(false);
  }
  expect(isResponse("authority.execute", accepted)).toBe(false);
  expect(
    isResponse("session", {
      endpoint: "session",
      outcome: { tag: "ACCEPTED", data: accepted.outcome.data },
    }),
  ).toBe(false);
});
