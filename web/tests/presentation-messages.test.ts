import { expect, it } from "vitest";
import { localNotice, type DiagnosticNotice, type LocalNoticeReason } from "../src/api/notices";
import {
  hasMessage,
  messageShapes as shapes,
  renderKey,
  renderIsolatedValue,
  translate,
} from "../src/presentation/messages";
import { renderNotice } from "../src/presentation/notice";
import { defaults, languages } from "../src/presentation/preferences";
import { diagnosticParameters as requirements } from "@contracts/messages";

it("renders every catalog key in every language and preserves typed interpolation contracts", () => {
  for (const language of languages) {
    for (const [key, shape] of Object.entries(shapes)) {
      const args = Object.fromEntries(Object.keys(shape).map((name) => [name, "CANONICAL-123"]));
      expect(hasMessage(key)).toBe(true);
      expect(renderKey({ ...defaults, language }, key, args)).not.toBe("");
    }
  }
  expect(hasMessage("missing.key")).toBe(false);
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(translate({ ...defaults, language: "lv" }, "ui.cases")).toBe("Lietas");
});

it("preserves pseudolocalization of literal templates and their arguments", () => {
  expect(translate({ ...defaults, language: "en-XA" }, "field.caseReference.label")).toMatch(
    /^⟦.+⟧$/u,
  );
  expect(
    translate({ ...defaults, language: "en-XA" }, "notice.accepted", {
      operationId: "synthetic-id",
    }),
  ).toContain("synthetic-id");
});

it("refuses unknown or malformed message arguments without echoing the supplied material", () => {
  const fallback = translate(defaults, "notice.unknownDiagnostic");
  for (const args of [{}, { operationId: 1 }, { operationId: "secret", extra: "secret" }]) {
    expect(renderKey(defaults, "ui.operationAccepted", args)).toBe(fallback);
  }
  for (const key of ["unexpected-secret-key", "constructor", "toString", "__proto__"]) {
    expect(renderKey(defaults, key)).toBe(fallback);
  }
  const canonical = "<unsafe>\u0308-CANONICAL";
  expect(
    translate({ ...defaults, language: "ar" }, "ui.reference", {
      label: "Reference",
      reference: canonical,
    }),
  ).toContain(`\u2068${canonical}\u2069`);
  expect(
    translate({ ...defaults, language: "en-XA" }, "ui.reference", {
      label: "Reference",
      reference: canonical,
    }),
  ).toContain(canonical);
});

it("renders every specific diagnostic using only its closed safe parameter contract", () => {
  const rules: Readonly<
    Record<string, Readonly<Record<string, { minimum?: number; enum?: ReadonlyArray<string> }>>>
  > = requirements;
  for (const [id, parameters] of Object.entries(rules)) {
    const values = Object.fromEntries(
      Object.entries(parameters).map(([key, range]) => [
        key,
        range.enum?.[0] ?? range.minimum ?? 0,
      ]),
    );
    const diagnostic = { code: id.slice(id.indexOf(".") + 1), limits: values } as DiagnosticNotice;
    for (const language of languages) {
      const p = { ...defaults, language };
      expect(renderNotice(p, { kind: "diagnostic", diagnostic })).not.toBe(
        translate(p, "notice.unknownDiagnostic"),
      );
    }
  }
  for (const parameters of [
    { extra: 1 },
    { maximumCharacters: -1 },
    { maximumCharacters: 2147483648 },
    { maximumCharacters: 1.5 },
  ]) {
    const diagnostic = { code: "INPUT_TEXT_TOO_LONG", limits: parameters } as DiagnosticNotice;
    expect(renderNotice(defaults, { kind: "diagnostic", diagnostic })).toBe(
      translate(defaults, "notice.unknownDiagnostic"),
    );
  }
  expect(
    renderNotice(defaults, {
      kind: "diagnostic",
      diagnostic: { code: "secret", limits: {} } as unknown as DiagnosticNotice,
    }),
  ).toBe(translate(defaults, "notice.unknownDiagnostic"));
});

it("retains local, accepted and uncertain notices as language-neutral data", () => {
  const reasons: LocalNoticeReason[] = [
    "unreachable",
    "incomplete",
    "cancelled",
    "definitionMismatch",
    "invalidSession",
    "missingCsrf",
    "responseTimeout",
    "outcomeUnknown",
  ];
  for (const reason of reasons) {
    expect(renderNotice(defaults, localNotice(reason))).not.toBe("");
  }
  const notice = localNotice("outcomeUnknown");
  const original = JSON.stringify(notice);
  expect(renderNotice(defaults, notice)).toContain("unknown");
  expect(renderNotice({ ...defaults, language: "lv" }, notice)).not.toBe(
    renderNotice(defaults, notice),
  );
  expect(JSON.stringify(notice)).toBe(original);
  expect(renderNotice(defaults, { kind: "invalidHttp", status: 413 })).toContain("413");
  expect(renderNotice(defaults, { kind: "accepted", operationId: "unchanged-id" })).toContain(
    "unchanged-id",
  );
});

it("refuses reference message parts with missing, wrong-role or foreign holes [CC-WEB-001]", () => {
  const preferences = { language: "en" as const, displayLocale: "en-GB" as const };
  const fallback = [renderKey(preferences, "notice.unknownDiagnostic")];
  const reference = { exact: "Synthetic reference" };
  expect(renderIsolatedValue(preferences, "ui.caseRow", {}, reference, "status")).toEqual(fallback);
  expect(
    renderIsolatedValue(preferences, "ui.caseRow", { status: 1, revision: 1 }, reference, "status"),
  ).toEqual(fallback);
  expect(
    renderIsolatedValue(
      preferences,
      "ui.reference",
      { reference: "S", extra: "foreign" },
      reference,
    ),
  ).toEqual(fallback);
  expect(renderIsolatedValue(preferences, "label.action", {}, reference)).toEqual(fallback);
});

it("requires every supplied message argument to be an exact own property", () => {
  const inherited: Readonly<Record<string, string>> = { unrelated: "foreign" };
  Object.setPrototypeOf(inherited, { reference: "inherited" });
  expect(renderIsolatedValue(defaults, "ui.reference", inherited, { exact: "inherited" })).toEqual([
    renderKey(defaults, "notice.unknownDiagnostic"),
  ]);
  expect(renderKey(defaults, "ui.reference", { label: "Reference", reference: "own" })).toBe(
    "Reference: own",
  );
  expect(
    renderKey(defaults, "ui.reference", {
      label: "Reference",
      reference: "own",
      unrelated: "foreign",
    }),
  ).toBe(renderKey(defaults, "notice.unknownDiagnostic"));
});
