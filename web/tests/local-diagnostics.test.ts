// @vitest-environment node
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import Ajv2020, { type ValidateFunction } from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { describe, expect, it } from "vitest";
import { isHostFailure } from "../src/generated/convergence/web-v2.validation";
import { webCases } from "./contract-corpus.fixtures";

const root = resolve(import.meta.dirname, "../src/generated/convergence");
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const read = (name: string): Record<string, unknown> => {
  const value: unknown = JSON.parse(readFileSync(resolve(root, name), "utf8"));
  if (!object(value)) throw new Error(`Expected generated object ${name}.`);
  return value;
};
const schemas = [
  ["cliProcess", "cli-v3.process-failure.schema.json"],
  ["webProcess", "web-v2.process-failure.schema.json"],
  ["administration", "administration-v1.response.schema.json"],
] as const;
const validators = (): ReadonlyMap<string, ValidateFunction> => {
  const ajv = new Ajv2020({
    allErrors: false,
    coerceTypes: false,
    ownProperties: true,
    removeAdditional: false,
    strict: true,
    useDefaults: false,
    validateFormats: true,
  });
  addFormats(ajv);
  return new Map(schemas.map(([surface, file]) => [surface, ajv.compile(read(file))]));
};

const parsedCase = (item: unknown) => {
  if (
    !object(item) ||
    typeof item["id"] !== "string" ||
    typeof item["surface"] !== "string" ||
    typeof item["valid"] !== "boolean"
  )
    throw new Error("Malformed diagnostic corpus item.");
  return { id: item["id"], surface: item["surface"], valid: item["valid"], value: item["value"] };
};

describe("local diagnostic contracts", () => {
  it("validates every process and administration cause and hostile counterpart", () => {
    const compiled = validators();
    const corpus = read("local-diagnostics.parsed-value-corpus.json");
    const cases: unknown = corpus["cases"];
    if (!Array.isArray(cases)) throw new Error("Missing diagnostic cases.");
    const counts = new Map<string, { positive: number; negative: number }>();
    for (const item of cases) {
      const example = parsedCase(item);
      const surface = example.surface;
      const validate = compiled.get(surface);
      if (validate === undefined) throw new Error(`Unregistered surface ${surface}.`);
      expect(validate(example.value), example.id + JSON.stringify(validate.errors)).toBe(
        example.valid,
      );
      const count = counts.get(surface) ?? { positive: 0, negative: 0 };
      if (example.valid) count.positive += 1;
      else count.negative += 1;
      counts.set(surface, count);
    }
    for (const [surface] of schemas) {
      expect(counts.get(surface)?.positive, surface).toBeGreaterThan(5);
      expect(counts.get(surface)?.negative, surface).toBeGreaterThan(20);
    }
  });

  it("fingerprints the administration grammar independently of display text", () => {
    const catalogue = read("administration-v1.catalog.json");
    const schema = read("administration-v1.response.schema.json");
    expect(catalogue["responseSchema"]).toEqual(schema);
    expect(catalogue["fingerprint"]).toBe(
      createHash("sha256").update(JSON.stringify(schema)).digest("hex"),
    );
    expect(JSON.stringify(schema)).not.toContain("could not");
  });

  it("rejects mismatched actual HTTP status even for an otherwise valid host body", async () => {
    const cases = webCases().filter((item) => item.valid && item.endpoint === null);
    expect(cases.length).toBeGreaterThan(20);
    for (const item of cases) {
      expect(await isHostFailure(item.value, item.status), item.id).toBe(true);
      expect(await isHostFailure(item.value, item.status === 400 ? 413 : 400), item.id).toBe(false);
    }
  });
});
