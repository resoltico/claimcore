import { messageShapes as coreShapes } from "@contracts/messages";
import { chromeShapes } from "./chromeMessages";
import { exactInteger } from "./format";
import { productMetadata } from "../protocol/productMetadata";
import { catalogFor } from "./languageCatalog";
import { presentationPseudolanguage } from "@contracts/client";
import { languageDirection } from "./preferences";
import { templateParts, pseudolocalizeLiteral, type TemplatePart } from "./templates";
import type { Preferences } from "./preferences";
import type { MessageKey, Values } from "./types";

export const messageShapes = { ...coreShapes, ...chromeShapes };
const shapes: Readonly<Record<string, Readonly<Record<string, string>>>> = messageShapes;
const cache = new Map<string, ReadonlyArray<TemplatePart>>();
const message = (key: MessageKey, preferences: Preferences): ReadonlyArray<TemplatePart> => {
  const identity = `${preferences.language}|${key}`;
  const cached = cache.get(identity);
  if (cached !== undefined) {
    return cached;
  }
  const pseudo = preferences.language === presentationPseudolanguage;
  const parts = templateParts(catalogFor(preferences.language)[key]!);
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
  key: MessageKey,
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

/** Admit a key arriving in independently loaded catalog metadata. */
const catalogMessageKey = (key: string): MessageKey | null => (hasMessage(key) ? key : null);
export const renderCatalogKey = (
  preferences: Preferences,
  key: string,
  values: Readonly<Record<string, string | number>> = {},
): string => {
  const argumentsForKey =
    key === "endpoint.operation.get.meaning" && Object.keys(values).length === 0
      ? {
          window: renderKey(preferences, "label.operationWindow", {
            minutes: exactInteger(
              String(productMetadata.catalog.limits.operationWindowMilliseconds / 60000),
              preferences.displayLocale,
            ),
          }),
        }
      : values;
  return renderKey(
    preferences,
    catalogMessageKey(key) ?? "notice.unknownDiagnostic",
    argumentsForKey,
  );
};

/** Retain catalog word order while rendering selected arguments as isolated DOM values. */
export const renderIsolatedValues = <T>(
  preferences: Preferences,
  key: MessageKey,
  values: Readonly<Record<string, string | number>>,
  references: Readonly<Record<string, T>>,
): (string | T)[] => {
  if (
    !safeArgs(key, values) ||
    Object.keys(references).some((argument) => shapes[key]?.[argument] !== "string")
  ) {
    return [renderKey(preferences, "notice.unknownDiagnostic")];
  }
  const result: (string | T)[] = [];
  for (const part of message(key, preferences)) {
    let value: string | T;
    if (part.kind === "literal") {
      ({ value } = part);
    } else if (Object.hasOwn(references, part.value)) {
      value = references[part.value]!;
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

export const renderIsolatedValue = <T>(
  preferences: Preferences,
  key: MessageKey,
  values: Readonly<Record<string, string | number>>,
  reference: T,
  argument = "reference",
): (string | T)[] => renderIsolatedValues(preferences, key, values, { [argument]: reference });
