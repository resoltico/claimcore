import { catalogs as generatedCatalogs, messageShapes } from "@contracts/messages";
import { presentationPseudolanguage, presentationDefaults } from "@contracts/client";
import { languageDirection } from "./preferences";
import { templateParts, pseudolocalizeLiteral, type TemplatePart } from "./templates";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

const catalogs: Readonly<Record<string, Readonly<Record<string, string>>>> = generatedCatalogs;
const shapes: Readonly<Record<string, Readonly<Record<string, string>>>> = messageShapes;
const cache = new Map<string, ReadonlyArray<TemplatePart>>();
const message = (key: string, preferences: Preferences): ReadonlyArray<TemplatePart> => {
  const identity = `${preferences.language}|${key}`;
  const cached = cache.get(identity);
  if (cached !== undefined) {
    return cached;
  }
  const pseudo = preferences.language === presentationPseudolanguage;
  const parts = templateParts(
    catalogs[pseudo ? presentationDefaults.language : preferences.language]![key]!,
  );
  const result: ReadonlyArray<TemplatePart> = pseudo
    ? [
        { kind: "literal", value: "⟦" },
        ...parts.map((part) =>
          part.kind === "literal" ? { ...part, value: pseudolocalizeLiteral(part.value) } : part,
        ),
        { kind: "literal", value: "⟧" },
      ]
    : parts;
  cache.set(identity, result);
  return result;
};
const safeArgs = (key: string, values: Readonly<Record<string, string | number>>): boolean => {
  const shape = Object.hasOwn(shapes, key) ? shapes[key] : undefined;
  return (
    shape !== undefined &&
    Object.keys(values).length === Object.keys(shape).length &&
    Object.entries(shape).every(
      ([name, role]) => Object.hasOwn(values, name) && typeof values[name] === role,
    )
  );
};
const isolate = (value: string | number, preferences: Preferences): string =>
  languageDirection(preferences.language) === "rtl" ? `\u2068${value}\u2069` : String(value);
export const renderKey = (
  preferences: Preferences,
  key: string,
  values: Readonly<Record<string, string | number>> = {},
): string => {
  const selected = safeArgs(key, values) ? key : "notice.unknownDiagnostic";
  return message(selected, preferences)
    .map((part) =>
      part.kind === "literal" ? part.value : isolate(values[part.value]!, preferences),
    )
    .join("");
};
export const translate = <K extends MessageKey>(
  preferences: Preferences,
  key: K,
  ...values: Values<K>
): string => renderKey(preferences, key, values[0] ?? {});
export const hasMessage = (key: string): key is MessageKey => Object.hasOwn(shapes, key);

/** Retain word order and substitute a DOM-isolated reference for the selected hole. */
export const renderIsolatedValue = <T>(
  preferences: Preferences,
  key: string,
  values: Readonly<Record<string, string | number>>,
  reference: T,
  argument = "reference",
): (string | T)[] => {
  if (!safeArgs(key, values) || shapes[key]?.[argument] !== "string") {
    return [renderKey(preferences, "notice.unknownDiagnostic")];
  }
  const result: (string | T)[] = [];
  for (const part of message(key, preferences)) {
    let value: string | T;
    if (part.kind === "literal") {
      ({ value } = part);
    } else if (part.value === argument) {
      value = reference;
    } else {
      value = isolate(values[part.value]!, preferences);
    }
    const previous = result.at(-1);
    if (typeof value === "string" && typeof previous === "string") {
      result[result.length - 1] = previous + value;
    } else {
      result.push(value);
    }
  }
  return result;
};
