import type { MessageValues } from "intl-messageformat";
import { expect, it, vi } from "vitest";
import { translate } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";

const observed = vi.hoisted(() => ({ constructions: 0 }));

// Observe construction while retaining the actual ICU parser and formatter implementation.
vi.mock(import("intl-messageformat"), async (importOriginal) => {
  const actual = await importOriginal();
  return {
    ...actual,
    default: class<V extends MessageValues | undefined = undefined> extends actual.default<V> {
      constructor(...parameters: ConstructorParameters<typeof actual.default>) {
        super(...parameters);
        observed.constructions += 1;
      }
    },
  };
});

it("reuses ICU formatters for literal templates and keeps language catalogs separate", () => {
  const before = observed.constructions;
  expect(translate(defaults, "notice.accepted", { operationId: "one" })).toBe(
    "Accepted exact operation one.",
  );
  expect(translate(defaults, "notice.accepted", { operationId: "two" })).toBe(
    "Accepted exact operation two.",
  );
  expect(observed.constructions).toBe(before + 1);
  expect(
    translate({ ...defaults, language: "lv" }, "notice.accepted", { operationId: "one" }),
  ).not.toBe("Accepted exact operation one.");
  expect(observed.constructions).toBe(before + 2);
});
