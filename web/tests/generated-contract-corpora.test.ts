// @vitest-environment node
import { describe, expect, it } from "vitest";

import { isHostFailure, isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import { webV2HostFailureStatuses } from "../src/generated/convergence/web-v2.endpoint-catalog";
import {
  cliCases,
  cliKindCoverage,
  cliCommandPrepareInputValidator,
  cliValidators,
  endpointInventory,
  rawCliInput,
  requiredWebEndpoint,
  webCases,
  webHostCoverage,
  webTagCoverage,
} from "./contract-corpus.fixtures";

const crossCount = (cases: readonly { readonly id: string }[]): number =>
  cases.filter((value) => value.id.startsWith("cross-")).length;

const replaceText = (value: unknown, expected: string, replacement: string): unknown => {
  if (typeof value === "string") return value === expected ? replacement : value;
  if (Array.isArray(value)) return value.map((item) => replaceText(item, expected, replacement));
  if (typeof value !== "object" || value === null) return value;
  return Object.fromEntries(
    Object.entries(value).map(([key, item]) => [key, replaceText(item, expected, replacement)]),
  );
};

const loneSurrogates = ["\ud800", "\udfff"] as const;

const validHostFailure = async (status: number, value: unknown): Promise<boolean> =>
  webV2HostFailureStatuses.some((candidate) => candidate === status) &&
  (await isHostFailure("session", value));

const compiledCliValidators = cliValidators();
const emittedHostCodes = [
  "WEB_BODY_TOO_LARGE",
  "WEB_BUSY",
  "WEB_CONNECTION_REJECTED",
  "WEB_CSRF_REJECTED",
  "WEB_INVALID_REQUEST",
  "WEB_LOGIN_REJECTED",
  "WEB_MEDIA_TYPE",
  "WEB_NOT_FOUND",
  "WEB_ORIGIN_REJECTED",
  "WEB_PROTOCOL",
  "WEB_SESSION_REJECTED",
] as const;

const expectWebCoverage = (): void => {
  for (const coverage of webTagCoverage()) {
    expect(coverage.actual, coverage.endpoint).toEqual(coverage.expected);
    expect(coverage.expected.length, coverage.endpoint).toBeGreaterThan(0);
  }
  const host = webHostCoverage();
  expect(host.statuses).toEqual([...webV2HostFailureStatuses].sort((left, right) => left - right));
  expect(host.phases).toEqual(["NOT_STARTED", "STARTED_UNCONFIRMED", "null"]);
  expect(host.codes).toEqual(expect.arrayContaining([...emittedHostCodes]));
};

const expectCliCases = (): void => {
  const cases = cliCases();
  expect(crossCount(cases)).toBe(15 * 14);
  expect(cases.filter((value) => value.id.startsWith("pattern-"))).toHaveLength(8);
  expect(cases.filter((value) => value.id.startsWith("scalar-"))).toHaveLength(27);
  expect(cases).toContainEqual(
    expect.objectContaining({ id: "valid-case-get-found-scalar-boundaries", valid: true }),
  );
  expect(cases).toContainEqual(
    expect.objectContaining({
      id: "valid-command-execute-preparation-state-unknown",
      exitCode: 4,
      valid: true,
    }),
  );
  expect(cases).toContainEqual(
    expect.objectContaining({
      id: "valid-recovery-resolve-cancelled-before-admission",
      exitCode: 130,
      valid: true,
    }),
  );

  for (const item of cases) {
    if (item.endpoint === null) {
      expect(compiledCliValidators.aggregate(item.value), item.id).toBe(item.valid);
      continue;
    }
    const endpoint = requiredWebEndpoint(item.endpoint);
    const validator = compiledCliValidators.endpoints.get(endpoint);
    if (validator === undefined) throw new Error(`Missing CLI validator ${endpoint}.`);
    expect(validator(item.value), item.id).toBe(item.valid);
    if (item.valid) expect(compiledCliValidators.aggregate(item.value), item.id).toBe(true);
  }
};

const expectCliCoverage = (): void => {
  for (const coverage of cliKindCoverage()) {
    expect(coverage.actual, coverage.endpoint).toEqual(coverage.expected);
    expect(coverage.expected.length, coverage.endpoint).toBeGreaterThan(0);
  }
};

const expectCliLoneSurrogates = (): void => {
  const source = cliCases().find((value) => value.id === "valid-case-get-found");
  const validate = compiledCliValidators.endpoints.get("case.get");
  if (source === undefined || validate === undefined)
    throw new Error("CLI scalar source and validator are required.");
  for (const value of loneSurrogates) {
    const malformed = replaceText(source.value, "SYNTHETIC-001", value);
    expect(validate(malformed), `CLI lone surrogate ${value.charCodeAt(0)}`).toBe(false);
  }
};

const expectCliRevisionBounds = (): void => {
  const validate = cliCommandPrepareInputValidator();
  for (const [identifier, accepted] of [
    ["valid-largest-allowed-revision", true],
    ["maximum-revision", false],
    ["overflow-revision", false],
  ] as const) {
    expect(validate(rawCliInput(identifier)), identifier).toBe(accepted);
  }
};

const expectWebCases = async (): Promise<void> => {
  const cases = webCases();
  expect(crossCount(cases)).toBe(endpointInventory.length * (endpointInventory.length - 1));
  expect(cases.filter((value) => value.id.startsWith("pattern-"))).toHaveLength(8);
  expect(cases.filter((value) => value.id.startsWith("scalar-"))).toHaveLength(27);
  expect(cases).toContainEqual(
    expect.objectContaining({ id: "valid-case-get-found-scalar-boundaries", valid: true }),
  );
  expectWebCoverage();

  for (const item of cases) {
    const actual =
      item.endpoint === null
        ? await validHostFailure(item.status, item.value)
        : await isWebV2Response(requiredWebEndpoint(item.endpoint), item.value);
    expect(actual, item.id).toBe(item.valid);
  }
};

const expectWebLoneSurrogates = async (): Promise<void> => {
  const source = webCases().find((value) => value.id === "valid-case-get-found");
  if (source === undefined) throw new Error("Web scalar source is required.");
  for (const value of loneSurrogates) {
    const malformed = replaceText(source.value, "SYNTHETIC-001", value);
    expect(
      await isWebV2Response("case.get", malformed),
      `Web lone surrogate ${value.charCodeAt(0)}`,
    ).toBe(false);
  }
};

describe("generated contract corpora", () => {
  it("accepts every production CLI branch and rejects malformed or cross-endpoint values", () => {
    expectCliCases();
    expectCliCoverage();
    expectCliLoneSurrogates();
    expectCliRevisionBounds();
  });

  it("accepts generated Web host values and rejects every malformed or cross-endpoint value", async () => {
    await expectWebCases();
    await expectWebLoneSurrogates();
  });
});
