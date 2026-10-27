import { it, expect } from "vitest";
import { endpoints } from "@contracts/client";
import { isResponse, isObject, isEndpointId, isHostFailure } from "../../../src/protocol/client";
import { corpusCases, wireReply, hostCases } from "../../support/contract-corpus.fixtures";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";
import { stripTypeScriptTypes } from "node:module";

const ordinal = (left: string, right: string): number => {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
};
it("all positive authored response families supply the consumed browser shape", () => {
  const reached = new Set<string>();
  for (const sample of corpusCases().filter(
    (candidate) => candidate.direction === "response" && candidate.valid,
  )) {
    expect(isResponse(sample.endpoint, sample.value), sample.id).toBe(true);
    reached.add(sample.endpoint);
    const incomplete: unknown = structuredClone(sample.value);
    if (!isObject(incomplete) || !isObject(incomplete["outcome"])) {
      throw new Error("Authored response example absent.");
    }
    delete incomplete["outcome"]["data"];
    expect(isResponse(sample.endpoint, incomplete), sample.id).toBe(false);
  }
  expect([...reached].sort(ordinal)).toEqual(
    endpoints.map((endpoint) => endpoint.id).sort(ordinal),
  );
});
const acceptedReceipt = () => {
  const accepted = wireReply("operation.execute");
  if (accepted.outcome.kind !== "ACCEPTED") {
    throw new Error("Case receipt fixture absent.");
  }
  return { ...accepted, outcome: accepted.outcome };
};
it("mutation guards require usable receipts and closed receipt grammar rather than only tags", () => {
  const accepted = acceptedReceipt();
  for (const data of [
    null,
    {},
    {
      operationId: accepted.outcome.data.operationId,
    },
    {
      ...accepted.outcome.data,
      position: null,
    },
    {
      ...accepted.outcome.data,
      subject: {
        ...accepted.outcome.data.subject,
        snapshot: {
          kind: "DISCLOSED",
        },
      },
    },
    {
      ...accepted.outcome.data,
      subject: {
        ...accepted.outcome.data.subject,
        revision: Number.MAX_SAFE_INTEGER + 1,
      },
    },
    {
      ...accepted.outcome.data,
      subject: "AUTHORITY",
    },
  ]) {
    expect(
      isResponse("operation.execute", {
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: {
          kind: "ACCEPTED",
          data,
        },
      }),
    ).toBe(false);
  }
  expect(isResponse("operation.execute", accepted)).toBe(true);
});
it("session replies reject accepted operation receipts", () => {
  const accepted = acceptedReceipt();
  expect(
    isResponse("session", {
      outcome: {
        kind: "ACCEPTED",
        data: accepted.outcome.data,
      },
    }),
  ).toBe(false);
});

type Sample = { endpoint: string; value: unknown; valid: boolean; response: boolean };
const examples = (file: string, response: boolean): Sample[] => {
  const parsed: unknown = JSON.parse(
    readFileSync(resolve(import.meta.dirname, "../../../../tests/fixtures", file), "utf8"),
  );
  if (!isObject(parsed) || !Array.isArray(parsed["cases"])) {
    throw new Error("Independent corpus is malformed.");
  }
  const rows: unknown[] = parsed["cases"];
  return rows.map((row) => {
    if (
      !isObject(row) ||
      typeof row["endpoint"] !== "string" ||
      !isEndpointId(row["endpoint"]) ||
      typeof row["valid"] !== "boolean" ||
      !Object.hasOwn(row, "value")
    ) {
      throw new Error("Independent example is malformed.");
    }
    return { endpoint: row["endpoint"], value: row["value"], valid: row["valid"], response };
  });
};
const mismatches = (source: string, samples: ReadonlyArray<Sample>): number => {
  const output: Record<string, unknown> = {};
  const compiled = `${stripTypeScriptTypes(source).replace(/^export\s+/gmu, "")}\nexports.isRequest=isRequest; exports.isResponse=isResponse;`;
  runInNewContext(compiled, { exports: output }, { timeout: 1000 });
  return samples.filter((sample) => {
    const check = output[sample.response ? "isResponse" : "isRequest"];
    if (typeof check !== "function") {
      throw new Error("The generated client omitted a production admission function.");
    }
    return (
      (Reflect.apply(check, undefined, [sample.endpoint, sample.value]) === true) !== sample.valid
    );
  }).length;
};

it("the independent wire corpus detects a defective emitted constraint", () => {
  const source = readFileSync(
    resolve(import.meta.dirname, "../../../../contracts/client.ts"),
    "utf8",
  );
  const corpus = [
    ...examples("wire-request-examples.json", false),
    ...examples("wire-response-examples.json", true),
  ];
  expect(corpus.some((sample) => sample.valid)).toBe(true);
  expect(corpus.some((sample) => !sample.valid)).toBe(true);
  expect(mismatches(source, corpus)).toBe(0);
  const broken = source.replaceAll("[0-9a-f]{8}", "[0-9a-f]{7}");
  expect(broken).not.toBe(source);
  expect(mismatches(broken, corpus)).toBeGreaterThan(0);
});

it("host failures bind actual HTTP status and forbid extra members", () => {
  for (const c of hostCases()) {
    expect(isHostFailure(c.value, c.status)).toBe(true);
    expect(isHostFailure(c.value, 403)).toBe(false);
    expect(
      isHostFailure({ hostFailure: { ...c.value.hostFailure, unexpected: 1 } }, c.status),
    ).toBe(false);
  }
});
