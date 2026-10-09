import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "./presentation-test-support";
import { editor, draftAt, current } from "./presentation-state.fixtures";
import type { CurrentCase } from "../src/api/v3";
import { response } from "./v3-ui.fixtures";
import { preferenceKey } from "../src/presentation/preferences";

const paidCase: CurrentCase = {
  ...current,
  case: {
    ...current.case,
    fields: {
      ...current.case.fields,
      paymentDecisionDate: "2026-09-03",
      payableAmount: "1",
      payableCurrency: "EUR",
      paymentDate: "2026-09-04",
    },
  },
  availableCommands: ["CORRECT_CASE"],
};

it.each(["en", "lv", "ar"])(
  "submits machine correction modes in %s [CC-DOM-002]",
  async (language) => {
    const user = userEvent.setup();
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
    );
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("Synthetic lost response")));
    render(
      editor({
        initialCommand: "CORRECT_CASE",
        current: paidCase,
      }),
    );
    const registration = document.querySelector<HTMLSelectElement>(
      "#correction-registration-mode",
    )!;
    const decision = document.querySelector<HTMLSelectElement>("#correction-decision-mode")!;
    const payment = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
    await user.selectOptions(registration, "REPLACE");
    await user.selectOptions(decision, "CLEAR");
    await user.selectOptions(payment, "CLEAR");
    const amount = document.querySelector<HTMLInputElement>(
      'input[data-field-name="claimedAmount"]',
    )!;
    await user.clear(amount);
    await user.type(amount, "1.0000");
    const languageSelector = document.querySelector<HTMLSelectElement>(
      '.presentation-controls select[id$="-language"]',
    )!;
    for (const next of ["ar", "lv", "en"]) {
      await user.selectOptions(languageSelector, next);
      expect(registration).toHaveValue("REPLACE");
      expect(decision).toHaveValue("CLEAR");
      expect(payment).toHaveValue("CLEAR");
      expect(amount).toHaveValue("1.0000");
      expect(globalThis.fetch).not.toHaveBeenCalled();
    }
    await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
    await waitFor(() => {
      expect(globalThis.fetch).toHaveBeenCalledOnce();
    });
    expect(draftAt(0).command).toMatchObject({
      kind: "CORRECT_CASE",
      groups: {
        registration: { mode: "REPLACE", values: { claimedAmount: "1.0000" } },
        decision: { mode: "CLEAR" },
        payment: { mode: "CLEAR" },
      },
    });
  },
);

it("associates payment acknowledgement with the generated mode control [CC-DOM-002]", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("command.prepare", "REJECTED", {
          operationId: draftAt(0).operationId,
          rejection: {
            code: "INVALID_INPUT",
            field: "payment",
            actualRevision: null,
            recommendedAction: "CORRECT_INPUT",
            message: "Synthetic payment acknowledgement refusal.",
            diagnostic: { id: "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED", parameters: {} },
          },
        }),
      ),
    ),
  );
  render(editor({ initialCommand: "CORRECT_CASE", current: paidCase }));
  const payment = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
  await user.selectOptions(
    document.querySelector<HTMLSelectElement>("#correction-decision-mode")!,
    "REPLACE",
  );
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  await waitFor(() => {
    expect(payment).toHaveFocus();
  });
  expect(payment).toHaveAttribute("aria-invalid", "true");
  const description = document.getElementById(payment.getAttribute("aria-describedby")!);
  expect(description).toHaveTextContent("explicit payment reaffirmation");
  await user.selectOptions(screen.getByRole("combobox", { name: "Interface language" }), "lv");
  expect(description).toHaveTextContent("skaidri jāapstiprina");
  await user.selectOptions(payment, "CLEAR");
  expect(payment).not.toHaveAttribute("aria-invalid");
  expect(payment).not.toHaveAttribute("aria-describedby");
  expect(globalThis.fetch).toHaveBeenCalledOnce();
});
