import { fireEvent } from "@testing-library/react";
import { productMetadata } from "@contracts/product-metadata";
import { caseFields } from "./service-foundation.fixtures";
/** Supplies wire-valid authored registration; individual tests override current business admission. */
export const fillRegistrationInputs = (overrides: Readonly<Record<string, string>> = {}): void => {
  const definition = productMetadata.descriptors.commands.find(
    (command) => command.kind === "OPEN",
  )!;
  const authored = new Map(Object.entries({ ...caseFields, ...overrides }));
  const names = ["caseReference", ...definition.inputs.fields.map((input) => input.fieldName)];
  for (const name of names) {
    const input = document.querySelector<HTMLInputElement>(`input[name="${name}"]`);
    if (input !== null) {
      fireEvent.change(input, { target: { value: authored.get(name) ?? "" } });
    }
  }
};
