import { expect, it, vi } from "vitest";
import { within } from "@testing-library/react";
import { render, screen } from "../support/presentation-test-support";
import { CatalogField } from "../../src/components/CatalogField";
import { productMetadata } from "../../src/protocol/productMetadata";
import { preferenceKey } from "../../src/presentation/preferences";
it("describes locale entry and derives the amount limit from the machine descriptor", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 2, language: "en", displayLocale: "lv-LV", theme: "LIGHT" }),
  );
  const amount = productMetadata.catalog.fields.find((field) => field.name === "claimedAmount")!;
  const date = productMetadata.catalog.fields.find((field) => field.name === "incidentDate")!;
  if (amount.scalar.kind !== "AMOUNT") {
    throw new Error("Amount descriptor missing.");
  }
  const descriptor = { ...amount, scalar: { ...amount.scalar, maximumFractionalDigits: 2 } };
  render(
    <>
      <CatalogField
        field={descriptor}
        inputName="amount"
        value="1,25"
        disabled={false}
        onChange={vi.fn()}
      />
      <CatalogField
        field={date}
        inputName="date"
        value="2026-09-01"
        disabled={false}
        onChange={vi.fn()}
      />
    </>,
  );
  const input = screen.getByRole("textbox", { name: "Amount claimed" });
  expect(input).toHaveValue("1,25");
  expect(input).toHaveAccessibleDescription(/selected number format.*up to 2 decimal places/u);
  expect(input).not.toHaveAccessibleDescription(/No.*commas/u);
  const segment = within(screen.getByRole("group", { name: "Incident date" })).getAllByRole(
    "spinbutton",
  )[0]!;
  expect(segment).toHaveAccessibleDescription(
    /Enter the date in these sections or choose it from the calendar/u,
  );
  expect(segment).not.toHaveAccessibleDescription(/Use ASCII digits in YYYY-MM-DD/u);
});
