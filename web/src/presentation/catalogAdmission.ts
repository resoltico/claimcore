import { templateParts } from "./templates";

export const admitCatalog = (
  raw: unknown,
  shapes: Readonly<Record<string, Readonly<Record<string, string>>>>,
): Readonly<Record<string, string>> => {
  if (raw === null || typeof raw !== "object" || Array.isArray(raw)) {
    throw new Error("Invalid language catalog");
  }
  const values: Record<string, string> = {};
  for (const [key, shape] of Object.entries(shapes)) {
    const text: unknown = Object.getOwnPropertyDescriptor(raw, key)?.value;
    if (typeof text !== "string") {
      throw new Error("Incomplete language catalog");
    }
    const names = new Set(
      templateParts(text)
        .filter((part) => part.kind !== "literal")
        .map((part) => part.value),
    );
    if (
      names.size !== Object.keys(shape).length ||
      Object.keys(shape).some((name) => !names.has(name))
    ) {
      throw new Error("Invalid language arguments");
    }
    values[key] = text;
  }
  return Object.freeze(values);
};
