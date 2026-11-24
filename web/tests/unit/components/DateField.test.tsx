import { useState } from "react";
import { expect, it, vi } from "vitest";
import { IconSprite } from "../../../src/ui/icons";
import { DateField } from "../../../src/ui/DateField";
import { productMetadata } from "../../../src/protocol/productMetadata";
import { fireEvent, render, screen } from "../../support/presentation-test-support";
import { changeField } from "../../support/field-entry";
it("authors segmented locale dates as ISO wire values", () => {
  const change = vi.fn();
  const field = productMetadata.catalog.fields.find(
    (candidate) => candidate.name === "incidentDate",
  )!;
  const Controlled = () => {
    const [value, setValue] = useState("2026-09-01");
    return (
      <DateField
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
  render(
    <>
      <IconSprite />
      <Controlled />
    </>,
  );
  changeField("Incident date", "2025-08-17");
  expect(change).toHaveBeenLastCalledWith("2025-08-17");
  const calendar = screen.getByRole("button", { name: "Calendar: Incident date" });
  fireEvent.focus(screen.getByRole("spinbutton", { name: /year/u }));
  expect(screen.queryByText(/Use YYYY-MM-DD/u)).not.toBeInTheDocument();
  expect(calendar).toBeVisible();
  expect(calendar.querySelector("use")).toHaveAttribute("href", "#claimcore-icon-calendar");
  expect(screen.queryByText("▦")).toBeNull();
});
