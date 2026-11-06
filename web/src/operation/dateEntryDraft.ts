/** Transient widget segments; these are never business dates or prepared request values. */
export type DateEntryPart = Readonly<{ type: "year" | "month" | "day"; value: number }>;
export type DateEntryDraft = Readonly<{ canonical: string; parts: ReadonlyArray<DateEntryPart> }>;
export type DateEntryDrafts = Readonly<Record<string, DateEntryDraft>>;
export const sameDateEntryDraft = (
  left: DateEntryDraft | undefined,
  right: DateEntryDraft | null,
) => {
  if (left === undefined || right === null) {
    return left === undefined && right === null;
  }
  return (
    left.canonical === right.canonical &&
    left.parts.length === right.parts.length &&
    left.parts.every(
      (part, index) =>
        part.type === right.parts[index]?.type && part.value === right.parts[index]?.value,
    )
  );
};
export const withoutDateEntries = (
  entries: DateEntryDrafts,
  paths: ReadonlyArray<string>,
): DateEntryDrafts => {
  if (!paths.some((path) => Object.hasOwn(entries, path))) {
    return entries;
  }
  const next = { ...entries };
  for (const path of paths) {
    delete next[path];
  }
  return next;
};
