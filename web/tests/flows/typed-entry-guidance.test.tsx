import { expect, it, vi } from "vitest";
import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { CatalogField } from "../../src/components/CatalogField";
import { productMetadata } from "../../src/protocol/productMetadata";
import { preferenceKey } from "../../src/presentation/preferences";
it("uses core widget hints and keeps precise formatting rules behind explicit help", async () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "en",
      displayLocale: "lv-LV",
      mode: "light",
      accent: "green",
    }),
  );
  const amount = productMetadata.catalog.fields.find((field) => field.name === "claimedAmount")!;
  const date = productMetadata.catalog.fields.find((field) => field.name === "incidentDate")!;
  render(
    <>
      <CatalogField
        field={amount}
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
  expect(input).toHaveAccessibleDescription("How much the claimant is asking for.");
  expect(input).not.toHaveAccessibleDescription(/ASCII|YYYY|redundant|decimal point/u);
  const segment = within(screen.getByRole("group", { name: "Incident date" })).getAllByRole(
    "spinbutton",
  )[0]!;
  expect(segment).toHaveAccessibleDescription(/When the incident happened/u);
  expect(segment).not.toHaveAccessibleDescription(/ASCII|YYYY/u);
  const help = screen.getByLabelText("Explain fields: Amount claimed", { exact: true });
  await userEvent.click(help);
  expect(help.closest("details")).toHaveAttribute("open");
  expect(help.closest("details")).toHaveTextContent(
    "up to 18 digits before the decimal point and up to 4 after it",
  );
});
