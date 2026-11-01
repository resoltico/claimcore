import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { CatalogInputs } from "../../src/components/CatalogInputs";
import { productMetadata } from "../../src/protocol/productMetadata";
import { render, screen } from "../support/presentation-test-support";

it("renders finite worklist sets as independent catalogue-labelled checkboxes", async () => {
  const change = vi.fn();
  const entry = productMetadata.catalog.entries.find((item) => item.endpoint === "case.list")!;
  render(
    <CatalogInputs
      entry={entry}
      values={{ lifecycle: ["ACTIVE"], progress: ["DECIDED"] }}
      current={null}
      disabled={false}
      fieldErrors={{}}
      onChange={change}
      onFieldsChange={vi.fn()}
    />,
  );
  const active = screen.getByRole("checkbox", { name: "Active" });
  expect(active).toBeChecked();
  await userEvent.setup().click(active);
  expect(change).toHaveBeenCalledWith("lifecycle", []);
  await userEvent.setup().click(screen.getByRole("checkbox", { name: "Voided" }));
  expect(change).toHaveBeenCalledWith("lifecycle", ["ACTIVE", "VOIDED"]);
});
