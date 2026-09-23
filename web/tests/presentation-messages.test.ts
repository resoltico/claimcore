import { expect, it } from "vitest";
import {
  localNotice,
  recoveryNotice,
  type Diagnostic,
  type LocalNoticeReason,
} from "../src/api/notices";
import { hasMessage, renderKey, translate } from "../src/presentation/messages";
import { renderNotice } from "../src/presentation/notice";
import { defaults, languages } from "../src/presentation/preferences";
import shapes from "../src/presentation/generated/arguments.json";
import requirements from "../src/presentation/generated/diagnostic-parameters.json";

it("renders every catalog key in every language and preserves typed interpolation contracts", () => {
  for (const language of languages) {
    for (const [key, shape] of Object.entries(shapes)) {
      const args = Object.fromEntries(
        Object.entries(shape).map(([name, role]) => [
          name,
          role === "number" ? 2 : "CANONICAL-123",
        ]),
      );
      expect(hasMessage(key)).toBe(true);
      expect(renderKey({ ...defaults, language }, key, args)).not.toBe("");
    }
  }
  expect(hasMessage("missing.key")).toBe(false);
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(translate(defaults, "ui.cases")).toBe("Cases");
  expect(translate({ ...defaults, language: "lv" }, "ui.cases")).toBe("Lietas");
});

it("keeps ICU plural grammar independent from the number-display locale", () => {
  const p = { language: "ar" as const, displayLocale: "en-GB" as const };
  for (const count of [0, 1, 2, 3, 11, 100])
    expect(translate(p, "ui.attemptCount", { count })).not.toBe("");
  expect(translate(p, "ui.attemptCount", { count: 11 })).toContain("11");
  expect(translate({ ...p, displayLocale: "ar-EG" }, "ui.attemptCount", { count: 11 })).toContain(
    "١١",
  );
  expect(translate({ ...defaults, language: "en-XA" }, "ui.attemptCount", { count: 2 })).toContain(
    "2",
  );
  expect(translate({ ...defaults, language: "en-XA" }, "ui.caseReference")).toMatch(/^⟦.+⟧$/u);
});

it("refuses unknown or malformed message arguments without echoing the supplied material", () => {
  const fallback = translate(defaults, "notice.unknownDiagnostic");
  for (const args of [{}, { operationId: 1 }, { operationId: "secret", extra: "secret" }])
    expect(renderKey(defaults, "ui.operationAccepted", args)).toBe(fallback);
  for (const count of [-1, 1.2, Infinity, NaN, Number.MAX_SAFE_INTEGER + 1])
    expect(renderKey(defaults, "ui.attemptCount", { count })).toBe(fallback);
  for (const key of ["unexpected-secret-key", "constructor", "toString", "__proto__"])
    expect(renderKey(defaults, key)).toBe(fallback);
  const canonical = "<unsafe>\u0308-CANONICAL";
  expect(
    translate({ ...defaults, language: "ar" }, "ui.reference", { reference: canonical }),
  ).toContain(`\u2068${canonical}\u2069`);
  expect(
    translate({ ...defaults, language: "en-XA" }, "ui.reference", { reference: canonical }),
  ).toContain(canonical);
});

it("renders every specific diagnostic using only its closed safe parameter contract", () => {
  for (const [id, parameters] of Object.entries(requirements)) {
    const values = Object.fromEntries(
      Object.entries(parameters).map(([key, range]) => [key, range.minimum ?? 0]),
    );
    const diagnostic = { id, parameters: values } as Diagnostic;
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
    const diagnostic = { id: "INPUT_TEXT_TOO_LONG", parameters } as Diagnostic;
    expect(renderNotice(defaults, { kind: "diagnostic", diagnostic })).toBe(
      translate(defaults, "notice.unknownDiagnostic"),
    );
  }
  expect(
    renderNotice(defaults, {
      kind: "diagnostic",
      diagnostic: { id: "secret", parameters: {} } as unknown as Diagnostic,
    }),
  ).toBe(translate(defaults, "notice.unknownDiagnostic"));
});

it("retains local, accepted and uncertain notices as language-neutral data", () => {
  const reasons: LocalNoticeReason[] = [
    "unreachable",
    "fileUnreadable",
    "exportInvalid",
    "incomplete",
    "definitionMismatch",
    "invalidSession",
    "missingCsrf",
    "dismissed",
    "alreadyDismissed",
    "retained",
    "existing",
    "digestUnavailable",
    "exportStarted",
    "keptForRecovery",
  ];
  for (const reason of reasons) expect(renderNotice(defaults, localNotice(reason))).not.toBe("");
  const notice = recoveryNotice(localNotice("unreachable"), "inspectBeforeAction");
  const original = JSON.stringify(notice);
  expect(renderNotice(defaults, notice)).toContain(
    "Inspect Recovery before taking another action.",
  );
  expect(renderNotice({ ...defaults, language: "lv" }, notice)).not.toContain("Inspect Recovery");
  expect(JSON.stringify(notice)).toBe(original);
  expect(renderNotice(defaults, { kind: "invalidHttp", status: 413 })).toContain("413");
  expect(renderNotice(defaults, { kind: "fileTooLarge", maximumBytes: 1000 })).toContain("1,000");
  expect(renderNotice(defaults, { kind: "accepted", operationId: "unchanged-id" })).toContain(
    "unchanged-id",
  );
});
