import { fireEvent } from "@testing-library/react";
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
    const input = document.querySelector<HTMLInputElement>(`input[data-field-name="${name}"]`);
    if (input !== null) {
      fireEvent.change(input, { target: { value: authored.get(name) ?? "" } });
    }
  }
};
