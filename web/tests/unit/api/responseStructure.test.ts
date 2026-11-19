import specification from "../../../../contracts/openapi.json";
import { describe, expect, it } from "vitest";
import {
  isResponseStructure as admits,
  isEndpointResponse,
  type ResponseSchema,
} from "../../../../contracts/client";

// Small authored constructor cases, not copies of endpoint response schemas.
const text = ["string", null, null, null, null, null] as const;
const token = [
  "oneOf",
  [
    ["const", "A"],
    ["const", "B"],
  ],
] as const;
const rejected = ["object", { key: token }, []] as const;
const partition: ResponseSchema = [
  "partition",
  ["object", { selected: ["array", token, 0, 2], rejected: ["array", rejected, 0, 2] }, []],
  ["A", "B"],
  "selected",
  "rejected",
  "key",
];

describe("generated finite structural interpreter", () => {
  it.each<{ shape: ResponseSchema; good: unknown; bad: unknown[] }>([
    { shape: ["jsonObject"], good: { free: "member" }, bad: [null, [], "object"] },
    {
      shape: ["object", { required: ["boolean"], optional: text }, ["optional"]],
      good: { required: false },
      bad: [{}, { required: true, extra: 1 }, { required: true, optional: null }],
    },
    {
      shape: ["object", { nullable: ["oneOf", [text, ["null"]]] }, []],
      good: { nullable: null },
      bad: [{}, { nullable: false }],
    },
    {
      shape: ["array", ["integer", -1, 1], 1, 2],
      good: [-1, 1],
      bad: [[], [0, 0, 0], [2], [0.5], {}],
    },
    {
      shape: ["integer", Number.MIN_SAFE_INTEGER, Number.MAX_SAFE_INTEGER],
      good: Number.MAX_SAFE_INTEGER,
      bad: [Number.MAX_SAFE_INTEGER + 1, NaN, Infinity, "1"],
    },
    { shape: ["boolean"], good: false, bad: [0, "false", null] },
    { shape: ["null"], good: null, bad: [undefined, false, {}] },
    { shape: ["const", "A"], good: "A", bad: ["a", "A\n", null] },
    {
      shape: [
        "oneOf",
        [
          ["object", { kind: ["const", "A"], value: text }, []],
          ["object", { kind: ["const", "B"] }, []],
        ],
      ],
      good: { kind: "A", value: "retained" },
      bad: [{ kind: "C" }, { kind: "B", value: "extra" }, { kind: "A" }],
    },
    { shape: ["oneOf", [["boolean"], ["boolean"]]], good: undefined, bad: [true] },
    {
      shape: ["ref", "Uuid"],
      good: "10000000-0000-7000-8000-000000000001",
      bad: [
        "00000000-0000-0000-0000-000000000000",
        "A0000000-0000-7000-8000-000000000001",
        "10000000-0000-7000-8000-000000000001\n",
      ],
    },
    { shape: ["ref", "NotAPublishedDefinition"], good: undefined, bad: [{}] },
    {
      shape: partition,
      good: { selected: ["B"], rejected: [{ key: "A" }] },
      bad: [
        { selected: ["A"], rejected: [] },
        { selected: ["A", "A"], rejected: [] },
        { selected: ["A"], rejected: [{ key: "A" }] },
        { selected: ["A", "B"], rejected: [{ key: "A" }] },
        { selected: ["A"], rejected: [{ key: "C" }] },
        { selected: ["A"], rejected: ["B"] },
      ],
    },
  ])(
    "admits constructor $shape.0 and refuses its structural counterexamples",
    ({ shape, good, bad }) => {
      // Undefined marks a deliberately uninhabitable/unknown descriptor, not an admitted value.
      if (good !== undefined) {
        expect(admits(shape, good)).toBe(true);
      }
      for (const value of bad) {
        expect(admits(shape, value)).toBe(false);
      }
    },
  );

  it("every published pattern compiles in the browser regex language", () => {
    const patterns = new Set<string>();
    const visit = (node: unknown): void => {
      if (node === null || typeof node !== "object") {
        return;
      }
      if (Array.isArray(node)) {
        for (const child of node) {
          visit(child);
        }
        return;
      }
      for (const [key, value] of Object.entries(node)) {
        if (key === "pattern" && typeof value === "string") {
          patterns.add(value);
        } else {
          visit(value);
        }
      }
    };
    visit(specification);
    expect(patterns.size).toBeGreaterThan(0);
    for (const pattern of patterns) {
      expect(() => new RegExp(pattern, "u")).not.toThrow();
    }
  });

  it("partitions admit all-selected, all-rejected and either order", () => {
    for (const value of [
      { selected: ["B", "A"], rejected: [] },
      { selected: [], rejected: [{ key: "B" }, { key: "A" }] },
      { selected: ["A"], rejected: [{ key: "B" }] },
    ]) {
      expect(admits(partition, value)).toBe(true);
    }
  });

  // Actual published people.requests -> AccessRequest -> Principal.HUMAN root; no parallel schema.
  const principalResponse = (issuer: string) => ({
    representation: "DATA",
    outcome: {
      kind: "SUCCEEDED",
      data: {
        basis: {
          observedAt: "2024-02-29T12:00:00.123456Z",
          businessDate: "2024-02-29",
          timeZone: "UTC",
        },
        items: [
          {
            principal: { kind: "HUMAN", issuer, subject: "synthetic-unicode-principal" },
            firstSeen: "2024-02-29T10:00:00.123456Z",
            lastSeen: "2024-02-29T11:00:00.123456Z",
            claims: {},
          },
        ],
        nextCursor: null,
      },
    },
  });
  it.each([
    "https://issuer.example/path/",
    "https://issuer.example/café/東京/",
    "https://issuer.example/😀/",
    "https://例え.test/身份/",
  ])(
    "admits declared ordinary Unicode issuer strings through the actual endpoint root: %s",
    (issuer) => {
      const response = principalResponse(issuer);
      expect(isEndpointResponse("people.requests", response)).toBe(true);
      expect(response.outcome.data.items[0]?.principal.issuer).toBe(issuer);
    },
  );
  it.each([
    ["Cc NUL U+0000", "\u0000"],
    ["Cc NEXT LINE U+0085", "\u0085"],
    ["Zs NO-BREAK SPACE U+00A0", "\u00a0"],
    ["Zs EM SPACE U+2003", "\u2003"],
    ["Zl LINE SEPARATOR U+2028", "\u2028"],
    ["Zp PARAGRAPH SEPARATOR U+2029", "\u2029"],
    ["explicit FEFF U+FEFF", "\ufeff"],
  ])("refuses excluded issuer category %s in both host and path", (_category, character) => {
    for (const issuer of [
      `https://issuer${character}.example/path/`,
      `https://issuer.example/path${character}/`,
    ]) {
      expect(isEndpointResponse("people.requests", principalResponse(issuer))).toBe(false);
    }
  });
  it.each([
    "prefixhttps://issuer.example/path/",
    "https://issuer.example/path/?query",
    "https://issuer.example/path/#fragment",
    "https://issuer.example/path/\n",
  ])("requires complete issuer pattern consumption at the actual endpoint root: %j", (issuer) => {
    expect(isEndpointResponse("people.requests", principalResponse(issuer))).toBe(false);
  });

  it("counts Unicode scalars separately from UTF-8 bytes and consumes patterns fully", () => {
    const scalar: ResponseSchema = ["string", null, null, 1, 1, null];
    const bytes: ResponseSchema = ["string", null, null, null, null, 4];
    expect(admits(scalar, "😀")).toBe(true);
    expect(admits(scalar, "e\u0301")).toBe(false);
    expect(admits(bytes, "😀")).toBe(true);
    expect(admits(bytes, "😀a")).toBe(false);
    expect(admits(bytes, "aaaa")).toBe(true);
    for (const value of ["\ud800", "\udc00", "a\ud800b"]) {
      expect(admits(text, value)).toBe(false);
    }
    const pattern: ResponseSchema = ["string", "^[A-Z]+$", null, null, null, null];
    expect(admits(pattern, "ABC")).toBe(true);
    for (const value of ["ABC\n", "ABC\r\n", "ABC\u2028", "aABC", "ABC😀"]) {
      expect(admits(pattern, value)).toBe(false);
    }
  });

  it("checks Gregorian dates without Date normalization and consumes format strings fully", () => {
    const date: ResponseSchema = ["string", null, "date", null, null, null];
    for (const value of ["2000-02-29", "2024-02-29", "0001-01-01", "9999-12-31"]) {
      expect(admits(date, value)).toBe(true);
    }
    for (const value of [
      "1900-02-29",
      "2023-02-29",
      "2024-02-30",
      "0000-01-01",
      "2024-00-01",
      "2024-13-01",
      "2024-01-00",
      "2024-04-31",
      "2024-02-29\n",
    ]) {
      expect(admits(date, value)).toBe(false);
    }
  });

  it("consumes canonical UUID format strings completely", () => {
    const uuid: ResponseSchema = ["string", null, "uuid", null, null, null];
    const value = "a0000000-0000-7000-8000-000000000001";
    expect(admits(uuid, value)).toBe(true);
    for (const invalid of [
      value.toUpperCase(),
      `${value}\n`,
      `${value}\u2028`,
      "00000000-0000-0000-0000-000000000000",
    ]) {
      expect(admits(uuid, invalid)).toBe(false);
    }
  });

  it("uses owning six and seven fraction roots without normalizing exact strings", () => {
    const six = "2024-02-29T23:59:59.123456Z";
    const seven = "2024-02-29T23:59:59.1234567Z";
    const instant: ResponseSchema = ["ref", "PresentedScalar.INSTANT"];
    const clock: ResponseSchema = ["ref", "DataRejection.OPERATION_CLOCK_UNAVAILABLE"];
    const rejection = (expiredBefore: string, observedAt = six) => ({
      code: "OPERATION_CLOCK_UNAVAILABLE",
      action: "STOP_AND_INVESTIGATE",
      phase: "NOT_STARTED",
      expiredBefore,
      observedAt,
    });
    const document = { kind: "INSTANT", value: six };
    expect(admits(instant, document)).toBe(true);
    expect(document.value).toBe(six);
    expect(admits(instant, { ...document, value: seven })).toBe(false);
    expect(admits(clock, rejection(seven))).toBe(true);
    expect(admits(clock, rejection(six))).toBe(false);
    expect(admits(clock, rejection(seven, seven))).toBe(false);
    const format: ResponseSchema = ["string", null, "date-time", null, null, null];
    for (const value of [six, seven]) {
      expect(admits(format, value)).toBe(true);
    }
    for (const value of [
      "2024-02-30T23:59:59.123456Z",
      "2024-02-29T24:00:00.123456Z",
      "2024-02-29T23:60:00.123456Z",
      "2024-02-29T23:59:60.123456Z",
      `${six}\n`,
      `${seven}\n`,
      "2024-02-29T23:59:59.12345Z",
      "2024-02-29T23:59:59.12345678Z",
    ]) {
      expect(admits(format, value)).toBe(false);
    }
  });
});

it("admits interleaved FACTS and refuses competing singleton screen placements", () => {
  const region = (role: string) => ({ role, content: { kind: "ENTRIES", entries: [] } });
  const facts = region("FACTS");
  const results = region("RESULTS");
  const notice = region("NOTICE");
  const screen: ResponseSchema = ["ref", "Screen"];
  expect(admits(screen, { regions: [facts, results, facts, notice] })).toBe(true);
  for (const singleton of [results, notice]) {
    expect(admits(screen, { regions: [singleton, facts, singleton] })).toBe(false);
  }
  expect(specification.components.schemas.Screen.properties.regions.allOf).toContainEqual({
    contains: { type: "object", required: ["role"], properties: { role: { const: "RESULTS" } } },
    minContains: 0,
    maxContains: 1,
  });
});
