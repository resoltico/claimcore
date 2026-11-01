import type { presentationDisplayLocales } from "@contracts/client";

/** Authored amounts retain the display locale in which their raw text was entered. */
export type LocalizedAmount = Readonly<{
  kind: "LOCALIZED_AMOUNT";
  text: string;
  locale: (typeof presentationDisplayLocales)[number];
}>;
export type FormValue = string | boolean | ReadonlyArray<string> | LocalizedAmount;
export type FormValues = Readonly<Record<string, FormValue>>;
export const isLocalizedAmount = (value: FormValue | undefined): value is LocalizedAmount =>
  typeof value === "object" && "kind" in value && value.kind === "LOCALIZED_AMOUNT";
export const isChoiceValues = (value: FormValue | undefined): value is ReadonlyArray<string> =>
  Array.isArray(value);
export const formValueText = (value: FormValue | undefined): string =>
  isLocalizedAmount(value) ? value.text : String(value ?? "");
