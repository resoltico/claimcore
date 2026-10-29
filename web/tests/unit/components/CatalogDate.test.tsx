import { useState } from "react";
import { expect, it, vi } from "vitest";
import { CatalogDate } from "../../../src/components/CatalogDate";
import { productMetadata } from "../../../src/protocol/productMetadata";
import { render, screen } from "../../support/presentation-test-support";
import { changeField } from "../../support/field-entry";
it("authors segmented locale dates as ISO wire values", () => {
  const change = vi.fn();
  const field = productMetadata.catalog.fields.find(
    (candidate) => candidate.name === "incidentDate",
  )!;
  const Controlled = () => {
    const [value, setValue] = useState("2026-09-01");
    return (
      <CatalogDate
        field={field}
        value={value}
        inputName="incidentDate"
        disabled={false}
        onChange={(next) => {
          change(next);
          setValue(next);
        }}
      />
    );
  };
  render(<Controlled />);
  changeField("Incident date", "2025-08-17");
  expect(change).toHaveBeenLastCalledWith("2025-08-17");
  expect(screen.getByRole("button", { name: "Open calendar: Incident date" })).toBeVisible();
});
