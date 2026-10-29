import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CatalogInputs } from "../../../src/components/CatalogInputs";
import { operationEntry } from "../../../src/operation/operationForm";
import { catalogs } from "@contracts/messages";

it("uses the binding vocabulary to label an option at an unfamiliar catalog path", () => {
  const original = operationEntry({ subject: "AUTHORITY", kind: "GRANT" });
  const role = original.inputs.find(
    (input) => input.kind.kind === "CHOICE" && input.kind.vocabulary === "role",
  )!;
  const input = {
    ...role,
    identity: "unfamiliar.selection",
    path: ["unfamiliar", "selection"],
    branches: [],
  };
  const entry = { ...original, inputs: [input], constants: [] };
  render(
    <CatalogInputs
      entry={entry}
      values={{ "unfamiliar.selection": "CASE_READER" }}
      current={null}
      disabled={false}
      fieldErrors={{}}
      onChange={vi.fn()}
    />,
  );
  expect(screen.getByRole("radio", { name: catalogs.en["role.CASE_READER.label"] })).toBeChecked();
});
