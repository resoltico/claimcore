export const previewChannel = "claimcore-attachment-preview";

export const isPositiveInteger = (value: unknown): value is number =>
  typeof value === "number" && Number.isSafeInteger(value) && value > 0;

export const messageRecord = (
  value: unknown,
  keys: ReadonlyArray<string>,
): value is Record<string, unknown> =>
  typeof value === "object" &&
  value !== null &&
  Object.keys(value).length === keys.length &&
  keys.every((key) => Object.hasOwn(value, key));
