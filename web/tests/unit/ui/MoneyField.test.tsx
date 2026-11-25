import { expect, it, vi } from "vitest";
import { MoneyField } from "../../../src/ui/MoneyField";
import { render, screen } from "../../support/presentation-test-support";

it("joins amount and currency in one box with one visible hint and one shared error", () => {
  const { container } = render(
    <MoneyField
      label="Claimed amount"
      groupLabel="Claimed amount and currency"
      optional="(optional)"
      presence="Leave both blank, or enter both amount and currency."
      hint="Enter the claimed amount."
      rule={{ name: "claimedAmount", id: null }}
      amount={{
        id: "amount",
        fieldName: "claimedAmount",
        value: "10.00",
        onChange: vi.fn(),
      }}
      currency={{
        id: "currency",
        label: "Currency",
        hint: "Use a currency code.",
        value: "EUR",
        fieldName: "claimedCurrency",
        options: [{ id: "EUR", label: "EUR" }],
        onChange: vi.fn(),
      }}
      error="Enter a valid amount and currency."
      disabled={false}
    />,
  );
  const amount = screen.getByRole("textbox", { name: "Claimed amount" });
  const currency = screen.getByRole("combobox", { name: "Currency" });
  const box = container.querySelector(".money-field.control-box");
  expect(box?.contains(amount)).toBe(true);
  expect(box?.contains(currency)).toBe(true);
  for (const selector of [".money-field.control-box", ".field-label-row", ".field-error"]) {
    expect(container.querySelectorAll(selector)).toHaveLength(1);
  }
  expect(
    screen.getByRole("group", { name: "Claimed amount and currency" }),
  ).toHaveAccessibleDescription("Leave both blank, or enter both amount and currency.");
  expect(amount).toHaveAccessibleDescription(
    "Enter the claimed amount. Leave both blank, or enter both amount and currency. Enter a valid amount and currency.",
  );
  expect(currency).toHaveAccessibleDescription(
    "Use a currency code. Leave both blank, or enter both amount and currency. Enter a valid amount and currency.",
  );
});
