import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";
import { stripTypeScriptTypes } from "node:module";
import { expect, it } from "vitest";
import { isObject, isEndpointId } from "../src/protocol/client";

type Sample = { endpoint: string; value: unknown; valid: boolean; response: boolean };
const examples = (file: string, response: boolean): Sample[] => {
  const parsed: unknown = JSON.parse(
    readFileSync(resolve(import.meta.dirname, "../../tests/fixtures", file), "utf8"),
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
  const source = readFileSync(resolve(import.meta.dirname, "../../contracts/client.ts"), "utf8");
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
