import { expect, it } from "vitest";
import { type LocalNoticeReason } from "../../src/protocol/notices";
import {
  hasMessage,
  messageShapes as shapes,
  renderKey,
  renderCatalogKey,
  renderIsolatedValue,
  translate,
} from "../../src/presentation/messages";
import { renderNotice } from "../../src/presentation/notice";
import { defaults, languages } from "../../src/presentation/preferences";

it("renders every catalog key in every language and preserves typed interpolation contracts", () => {
  for (const language of languages) {
    for (const [key, shape] of Object.entries(shapes)) {
      const args = Object.fromEntries(Object.keys(shape).map((name) => [name, "CANONICAL-123"]));
      expect(hasMessage(key)).toBe(true);
      if (!hasMessage(key)) {
        throw new Error("Generated key was not admitted.");
      }
      const rendered = renderKey({ ...defaults, language }, key, args);
      expect(rendered).not.toBe("");
      if (key !== "notice.unknownDiagnostic") {
        expect(rendered).not.toBe(renderKey({ ...defaults, language }, "notice.unknownDiagnostic"));
      }
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
    expect(renderKey(defaults, "notice.accepted", args)).toBe(fallback);
  }
  for (const key of ["unexpected-secret-key", "constructor", "toString", "__proto__"]) {
    expect(renderCatalogKey(defaults, key)).toBe(fallback);
  }
  const canonical = "<unsafe>\u0308-CANONICAL";
  expect(
    translate({ ...defaults, language: "ar" }, "ui.actDescription", {
      label: "Reference",
      meaning: canonical,
    }),
  ).toContain(`\u2068${canonical}\u2069`);
  expect(
    translate({ ...defaults, language: "en-XA" }, "ui.actDescription", {
      label: "Reference",
      meaning: canonical,
    }),
  ).toContain(canonical);
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
    expect(renderNotice(defaults, { kind: "local", reason })).not.toBe("");
  }
  const notice = { kind: "local", reason: "outcomeUnknown" } as const;
  const original = JSON.stringify(notice);
  expect(renderNotice(defaults, notice)).toContain("could not confirm");
  expect(renderNotice({ ...defaults, language: "lv" }, notice)).not.toBe(
    renderNotice(defaults, notice),
  );
  expect(JSON.stringify(notice)).toBe(original);
  expect(renderNotice(defaults, { kind: "invalidHttp", status: 413 })).toContain("413");
  expect(renderNotice(defaults, { kind: "accepted", operationId: "unchanged-id" })).toContain(
    "unchanged-id",
  );
});

it("refuses reference message parts with missing, wrong-role or foreign holes", () => {
  const preferences = {
    language: "en" as const,
    displayLocale: "en-GB" as const,
    mode: "system" as const,
    accent: "green" as const,
  };
  const fallback = [renderKey(preferences, "notice.unknownDiagnostic")];
  const reference = { exact: "Synthetic reference" };
  expect(renderIsolatedValue(preferences, "ui.actDescription", {}, reference, "label")).toEqual(
    fallback,
  );
  expect(
    renderIsolatedValue(
      preferences,
      "ui.actDescription",
      { label: 1, meaning: 1 },
      reference,
      "label",
    ),
  ).toEqual(fallback);
  expect(
    renderIsolatedValue(
      preferences,
      "ui.actDescription",
      { meaning: "S", extra: "foreign" },
      reference,
    ),
  ).toEqual(fallback);
  expect(renderIsolatedValue(preferences, "label.action", {}, reference)).toEqual(fallback);
});

it("requires every supplied message argument to be an exact own property", () => {
  const inherited: Readonly<Record<string, string>> = { label: "Reference" };
  Object.setPrototypeOf(inherited, { meaning: "inherited" });
  expect(
    renderIsolatedValue(
      defaults,
      "ui.actDescription",
      inherited,
      { exact: "inherited" },
      "meaning",
    ),
  ).toEqual([renderKey(defaults, "notice.unknownDiagnostic")]);
  expect(renderKey(defaults, "ui.actDescription", { label: "Reference", meaning: "own" })).toBe(
    "Reference: own",
  );
  expect(
    renderKey(defaults, "ui.actDescription", {
      label: "Reference",
      meaning: "own",
      unrelated: "foreign",
    }),
  ).toBe(renderKey(defaults, "notice.unknownDiagnostic"));
});
