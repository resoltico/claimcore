import { expect, it } from "vitest";
import { definition } from "./service-ui.fixtures";
import { renderKey } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
const { descriptors } = definition;

import { readUnknown, isObject } from "./contract-corpus.fixtures";
import requirements from "../src/presentation/generated/diagnostic-parameters.json";
it("every F# diagnostic template agrees with default browser rendering", () => {
  const raw = readUnknown("descriptors.json");
  const originals = isObject(raw) ? raw["messages"] : null;
  if (!isObject(originals)) {
    throw new Error("Native presentation absent.");
  }
  const limits: Record<
    string,
    Record<string, { minimum?: number; maximum?: number; enum?: ReadonlyArray<string> }>
  > = requirements;
  const ids = Object.keys(limits);
  expect(ids).toHaveLength(66);
  for (const id of ids) {
    const parts = originals[`diagnostic.${id}`];
    if (!Array.isArray(parts)) {
      throw new Error("Native template absent.");
    }
    const values = Object.fromEntries(
      Object.entries(limits[id]!).map(([key, rule]) => [
        key,
        rule.enum?.[0] ?? String(rule.minimum ?? 0),
      ]),
    );
    const expected = parts
      .map((part: unknown) => {
        if (!isObject(part)) {
          throw new Error("Invalid native part.");
        }
        return typeof part["literal"] === "string" ? part["literal"] : values[String(part["hole"])];
      })
      .join("");
    expect(renderKey(defaults, `diagnostic.${id}`, values), id).toBe(expected);
  }
});
it("preserves independent English diagnostics and parameter formatting", () => {
  expect(renderKey(defaults, "diagnostic.INPUT_TEXT_TOO_LONG", { maximumCharacters: "200" })).toBe(
    "The value must not exceed 200 Unicode characters.",
  );
  expect(renderKey(defaults, "diagnostic.HOST_COMPLETEDRESPONSEFAILED")).toBe(
    "The completed response could not be delivered.",
  );
});

it("derives shared browser business meaning directly from static descriptors", () => {
  const p = createPresenter(defaults);
  for (const field of descriptors.fields) {
    expect(p.fieldLabel(field.name)).toBe(field.label);
    expect(p.fieldMeaning(field.name)).toBe(field.meaning);
  }
  for (const command of descriptors.commands) {
    expect(p.commandLabel(command.kind)).toBe(command.label);
    expect(p.commandMeaning(command.kind)).toBe(command.meaning);
    if (command.inputs.kind === "CORRECTION_GROUPS") {
      for (const group of command.inputs.groups) {
        expect(p.groupLabel(group.name)).toBe(group.label);
        expect(p.groupMeaning(group.name)).toBe(group.meaning);
      }
    }
  }
});
