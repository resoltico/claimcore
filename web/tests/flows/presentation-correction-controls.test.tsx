import { selectFact } from "../support/field-entry";
import { chooseOption, selectedOption } from "../support/choice-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { editor, draftAt, current } from "../support/presentation-state.fixtures";
import type { CurrentCase } from "../../src/api/service";
import { response } from "../support/service-ui.fixtures";
import { preferenceKey } from "../../src/presentation/preferences";

const paidCase: CurrentCase = {
  ...current,
  advice: { ...caseAdvice(["CORRECT"]), history: ["SUMMARY", "FULL"] },

  state: {
    ...current.state,

    fields: {
      ...current.state.fields,
      decision: { paymentDecisionDate: "2026-09-03", payableAmount: "1", payableCurrency: "EUR" },
      payment: { paymentDate: "2026-09-04" },
    },
  },
};

it.each(["en", "lv", "ar"])("submits machine correction modes in %s", async (language) => {
  const user = userEvent.setup();
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 2, language, displayLocale: "en-GB", theme: "SYSTEM" as const }),
  );
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("Synthetic lost response")));
  render(
    editor({
      initialAct: "CORRECT",
      current: paidCase,
    }),
  );
  const registration = document.querySelector<HTMLSelectElement>("#correction-registration-mode")!;
  const decision = document.querySelector<HTMLSelectElement>("#correction-decision-mode")!;
  const payment = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
  await chooseOption(user, registration, "UPDATE");
  await chooseOption(user, decision, "CLEAR");
  await chooseOption(user, payment, "CLEAR");
  selectFact("claimedAmount");
  const amount = document.querySelector<HTMLInputElement>(
    'input[data-field-name="claimedAmount"]',
  )!;
  await user.clear(amount);
  await user.type(amount, "1.0000");
  const languageSelector = document.querySelector<HTMLSelectElement>(
    '.presentation-controls select[id$="-language"]',
  )!;
  for (const next of ["ar", "lv", "en"]) {
    await chooseOption(user, languageSelector, next);
    expect(selectedOption(registration)).toBe("UPDATE");
    expect(selectedOption(decision)).toBe("CLEAR");
    expect(selectedOption(payment)).toBe("CLEAR");
    expect(amount).toHaveValue("1.0000");
    expect(globalThis.fetch).not.toHaveBeenCalled();
  }
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  await waitFor(() => {
    expect(globalThis.fetch).toHaveBeenCalledOnce();
  });
  expect(draftAt(0).act).toMatchObject({
    kind: "CORRECT",
    groups: {
      registration: { kind: "UPDATE", values: { claimed: { amount: "1.0000", currency: "EUR" } } },
      decision: { kind: "CLEAR" },
      payment: { kind: "CLEAR" },
    },
  });
});

it("associates payment acknowledgement with the generated mode control", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("operation.preview", "REJECTED", {
          code: "INVALID_INPUT",
          action: "CORRECT_INPUT",
          issues: [{ field: "payment", code: "PAYMENT_ACKNOWLEDGEMENT_REQUIRED" }],
        }),
      ),
    ),
  );
  render(editor({ initialAct: "CORRECT", current: paidCase }));
  const payment = document.querySelector<HTMLSelectElement>("#correction-payment-mode")!;
  await chooseOption(
    user,
    document.querySelector<HTMLSelectElement>("#correction-decision-mode")!,
    "UPDATE",
  );
  selectFact("paymentDecisionDate");
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  await waitFor(() => {
    expect(screen.getByRole("alert")).toHaveFocus();
  });
  expect(payment).toHaveAttribute("aria-invalid", "true");
  const description = document.getElementById("act.groups.payment.kind-error");
  expect(payment.getAttribute("aria-describedby")?.split(" ")).toEqual([
    "act.groups.payment.kind-meaning",
    "act.groups.payment.kind-error",
  ]);
  expect(description).toHaveTextContent("explicit payment reaffirmation");
  await chooseOption(user, screen.getByRole("combobox", { name: "Interface language" }), "lv");
  expect(description).toHaveTextContent("skaidri jāapstiprina");
  await chooseOption(user, payment, "CLEAR");
  expect(payment).not.toHaveAttribute("aria-invalid");
  expect(payment).toHaveAttribute("aria-describedby", "act.groups.payment.kind-meaning");
  expect(globalThis.fetch).toHaveBeenCalledOnce();
});
