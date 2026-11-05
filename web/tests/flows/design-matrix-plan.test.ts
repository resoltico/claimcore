// @vitest-environment node
import { expect, it } from "vitest";
import {
  assertMatrixPlan,
  assertMatrixCounts,
  dimensions,
  dimensionCaseTitle,
  selectDiagnosticDimension,
  expectedDimensionCounts,
  factReadabilityContext,
  expectedEngineCounts,
  expectedRoutes,
  screenshotDimension,
  capturesFrame,
  type Dimension,
} from "../../e2e/design-matrix-plan";
const scenes = [
  "cases-mine",
  "cases-notified",
  "cases-summary",
  "cases-open-undecided",
  "cases-decided-unpaid",
  "cases-recent",
  "cases-closed",
  "cases-voided",
  "cases-all",
  "cases-filtered",
  "case-undecided",
  "case-decided",
  "case-paid",
  "case-closed",
  "case-voided",
  "case-erased",
  "editor-open",
  "editor-decide",
  "editor-correct",
  "editor-void",
  "editor-work",
  "recovery",
  "paid-correction-review",
  "history-comparison",
  "operation-result",
  "authority-history",
  "not-found",
  "authority-list",
  "authority-detail",
];
const captures = [
  "cases-all",
  "cases-mine",
  "cases-summary",
  "case-decided",
  "case-voided",
  "case-erased",
  "editor-decide",
  "authority-list",
  "authority-detail",
  "authority-history",
  "not-found",
  "recovery",
  "editor-work",
  "paid-correction-review",
  "history-comparison",
];
it.each([
  ["chromium", 50, 1450, 1500, 272],
  ["firefox", 8, 232, 240, 0],
  ["webkit", 8, 232, 240, 0],
] as const)(
  "pins complete %s tuples and counts before dataset setup",
  (engine, count, loads, checks, shots) => {
    const required: Dimension[] = [];
    for (const width of engine === "chromium" ? [320, 390, 768, 1280] : [320, 1280]) {
      for (const language of engine === "chromium"
        ? (["en", "lv", "ar"] as const)
        : (["en", "ar"] as const)) {
        for (const mode of engine === "chromium"
          ? (["light", "dark"] as const)
          : (["light"] as const)) {
          for (const accent of ["green", "blue"] as const) {
            required.push({ width, language, mode, accent });
          }
        }
      }
    }
    if (engine === "chromium") {
      required.push(
        { width: 320, language: "en-XA", mode: "light", accent: "green" },
        { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 },
      );
    }
    expect(dimensions(engine)).toEqual(required);
    expect(new Set(required.map((d) => JSON.stringify(d))).size).toBe(count);
    expect(expectedEngineCounts(engine)).toEqual({
      dimensions: count,
      pageLoads: loads,
      axeAndReflowChecks: checks,
      screenshots: shots,
    });
    expect(() => {
      assertMatrixPlan(engine);
    }).not.toThrow();
  },
);
it("pins all 29 scenes and the extra filter check independently", () => {
  expect(expectedRoutes).toEqual(scenes);
  expect(new Set(scenes).size).toBe(29);
  expect([...scenes, "cases-filter-panel"]).toHaveLength(30);
});
it("captures EN/LV/AR at 390px in both modes and accents with the exact frame whitelist", () => {
  for (const language of ["en", "lv", "ar"] as const) {
    for (const mode of ["light", "dark"] as const) {
      for (const accent of ["green", "blue"] as const) {
        const d = { width: 390, language, mode, accent };
        expect(screenshotDimension(d)).toBe(true);
        const extra = language === "en" && mode === "light" ? ["cases-open-undecided"] : [];
        expect(scenes.filter((frame) => capturesFrame(frame, d)).sort()).toEqual(
          [...captures, ...extra].sort(),
        );
      }
    }
  }
});
it("retains English 320px/light and 1280px captures and excludes extras", () => {
  for (const d of dimensions("chromium")) {
    const wanted =
      d.zoom === undefined &&
      ((d.language === "en" && [390, 1280].includes(d.width)) ||
        (d.language === "en" && d.width === 320 && d.mode === "light") ||
        (["lv", "ar"].includes(d.language) && d.width === 390));
    expect(screenshotDimension(d)).toBe(wanted);
  }
});
it.each([
  ["chromium", { dimensions: 26, pageLoads: 754, axeAndReflowChecks: 780, screenshots: 91 }],
  ["firefox", { dimensions: 4, pageLoads: 116, axeAndReflowChecks: 120, screenshots: 0 }],
  ["webkit", { dimensions: 4, pageLoads: 116, axeAndReflowChecks: 120, screenshots: 0 }],
] as const)("rejects obsolete one-accent %s totals", (engine, old) => {
  expect(() => {
    assertMatrixCounts(engine, old);
  }).toThrow("independently pinned");
});

it("selects only the exact Native-owned diagnostic case and pins its partial counts", () => {
  const title = "en 640px green light at 200% zoom";
  const selected = selectDiagnosticDimension(dimensions("chromium"), title, "diagnostic");
  expect(selected).toEqual({ width: 640, language: "en", mode: "light", accent: "green", zoom: 2 });
  expect(dimensionCaseTitle(selected!)).toBe(title);
  expect(expectedDimensionCounts("chromium", selected!)).toEqual({
    dimensions: 1,
    pageLoads: 29,
    axeAndReflowChecks: 30,
    screenshots: 0,
  });
  expect(selectDiagnosticDimension(dimensions("chromium"), undefined, "complete")).toBeUndefined();
  expect(expectedEngineCounts("chromium")).toEqual({
    dimensions: 50,
    pageLoads: 1450,
    axeAndReflowChecks: 1500,
    screenshots: 272,
  });
});
it("refuses unknown duplicate cross-engine and nondiagnostic case selectors", () => {
  const zoom: Dimension = { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 };
  const title = "en 640px green light at 200% zoom";
  expect(() => selectDiagnosticDimension([zoom, zoom], title, "diagnostic")).toThrow("exactly one");
  expect(() => selectDiagnosticDimension(dimensions("firefox"), title, "diagnostic")).toThrow(
    "exactly one",
  );
  expect(() =>
    selectDiagnosticDimension(dimensions("chromium"), `${title} private`, "diagnostic"),
  ).toThrow("exactly one");
  expect(() => selectDiagnosticDimension(dimensions("chromium"), title, "complete")).toThrow(
    "diagnostic scope",
  );
  expect(() => selectDiagnosticDimension(dimensions("chromium"), "", "diagnostic")).toThrow(
    "exactly one",
  );
});
it("redacts every failing date into exact field frame and visibility metadata", () => {
  expect(
    factReadabilityContext("case-paid", [
      { field: "incidentDate", inert: false },
      { field: "paymentDate", inert: true },
    ]),
  ).toEqual({
    frame: "CASE_PAID",
    fields: [
      { field: "INCIDENT_DATE", inert: false },
      { field: "PAYMENT_DATE", inert: true },
    ],
    markers: [
      "E2E_FACT_READABILITY_INCIDENT_DATE_CASE_PAID_EXPOSED",
      "E2E_FACT_READABILITY_PAYMENT_DATE_CASE_PAID_INERT",
    ],
  });
});
it("rejects private appended or unknown readability identities", () => {
  expect(() =>
    factReadabilityContext("case-paid\nPRIVATE", [{ field: "paymentDate", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() =>
    factReadabilityContext("case-paid", [{ field: "paymentDate_PRIVATE", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() =>
    factReadabilityContext("case-paid", [{ field: "2026-11-04", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() => factReadabilityContext("case-paid", [])).toThrow("INVALID_METADATA");
});
