import { changeField } from "./field-entry";
import { caseFields } from "./service-foundation.fixtures";
/** Supplies wire-valid authored registration; individual tests override current business admission. */
export const fillRegistrationInputs = (overrides: Readonly<Record<string, string>> = {}): void => {
  const authored = new Map(
    Object.entries({
      ...caseFields.registration,
      caseReference: caseFields.caseReference,
      ...overrides,
    }),
  );
  for (const [name, value] of authored) {
    const input = document.querySelector<HTMLElement>(`[data-field-name="${name}"]`);
    if (input !== null) {
      changeField(input, value ?? "");
    }
  }
};
