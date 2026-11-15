import type { presentationDisplayLocales } from "@contracts/client";

/** Authored amounts retain the display locale in which their raw text was entered. */
export type LocalizedAmount = Readonly<{
  kind: "LOCALIZED_AMOUNT";
  text: string;
  locale: (typeof presentationDisplayLocales)[number];
}>;
/** Collection rows remain structured throughout editing, review and request assembly. */
export type FormCollection = Readonly<{
  kind: "COLLECTION";
  rows: ReadonlyArray<Readonly<{ key: string; values: FormValues }>>;
}>;
export type GrantTargetDraft = Readonly<{
  kind: "GRANT_TARGET";
  selection: { kind: "INSTALLATION" } | { kind: "REFERENCE" | "ID"; text: string };
}>;
export type FormValue =
  | null
  | string
  | number
  | boolean
  | ReadonlyArray<string>
  | LocalizedAmount
  | FormCollection
  | GrantTargetDraft;
export type FormValues = Readonly<Record<string, FormValue>>;
export const isLocalizedAmount = (value: FormValue | undefined): value is LocalizedAmount =>
  typeof value === "object" &&
  value !== null &&
  "kind" in value &&
  value.kind === "LOCALIZED_AMOUNT";
export const isFormCollection = (value: FormValue | undefined): value is FormCollection =>
  typeof value === "object" && value !== null && "kind" in value && value.kind === "COLLECTION";
export const formValueText = (value: FormValue | undefined): string => {
  if (isLocalizedAmount(value)) {
    return value.text;
  }
  if (Array.isArray(value)) {
    return value.join(",");
  }
  return typeof value === "object" && value !== null ? "" : String(value ?? "");
};
