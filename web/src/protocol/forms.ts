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
export type FormValue =
  null | string | boolean | ReadonlyArray<string> | LocalizedAmount | FormCollection;
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
  return isFormCollection(value) ? "" : String(value ?? "");
};
