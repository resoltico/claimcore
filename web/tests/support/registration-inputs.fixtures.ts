import { changeField } from "./field-entry";
import { productMetadata } from "../../src/protocol/productMetadata";
import { caseFields } from "./service-foundation.fixtures";
/** Supplies wire-valid authored registration; individual tests override current business admission. */
export const fillRegistrationInputs = (overrides: Readonly<Record<string, string>> = {}): void => {
  const definition = productMetadata.catalog.entries.find(
    (entry) => entry.endpoint === "operation.execute" && entry.kind === "OPEN",
  )!;
  const authored = new Map(
    Object.entries({
      ...caseFields.registration,
      caseReference: caseFields.caseReference,
      ...overrides,
    }),
  );
  const names = definition.inputs.flatMap((input) => (input.field === null ? [] : [input.field]));
  for (const name of names) {
    const input = document.querySelector<HTMLElement>(`[data-field-name="${name}"]`);
    if (input !== null) {
      changeField(input, authored.get(name) ?? "");
    }
  }
};
