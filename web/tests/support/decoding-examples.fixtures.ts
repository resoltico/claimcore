import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { EndpointId } from "@contracts/client";
const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export type DecodingExample = {
  id: string;
  endpoint: EndpointId;
  direction: "request" | "response";
  valid: boolean;
  value: unknown;
};
export const decodingExamples = (): DecodingExample[] => {
  const cases = (["request", "response"] as const).flatMap((direction) => {
    const raw: unknown = JSON.parse(
      readFileSync(
        resolve(import.meta.dirname, `../../../tests/fixtures/examples/${direction}s.json`),
        "utf8",
      ),
    );
    if (!isObject(raw) || !Array.isArray(raw["cases"])) {
      throw new Error("Authored examples absent.");
    }
    return raw["cases"].map((row: Record<string, unknown>) => ({
      id: String(row["id"] ?? row["name"]),
      endpoint: String(row["endpoint"]) as EndpointId,
      direction,
      valid: row["valid"] === true,
      value: row["value"],
    }));
  });
  return cases;
};

export const hostCases = () => [
  {
    id: "input-invalid",
    valid: true,
    endpoint: null,
    status: 400,
    value: { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" } },
  },
];
