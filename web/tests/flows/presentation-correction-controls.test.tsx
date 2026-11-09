import { selectFact } from "../support/field-entry";
import { chooseOption, selectedOption } from "../support/choice-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import {
  editor,
  draftAt,
  current,
  setEditorLanguage,
} from "../support/presentation-state.fixtures";
import type { CurrentCase } from "../../src/api/service";
import { response } from "../support/service-ui.fixtures";
import { preferenceKey } from "../../src/presentation/preferences";

const paidCase: CurrentCase = {
  ...current,
  advice: { ...caseAdvice(["CORRECT"]), history: "FULL" },

  state: {
    ...current.state,

    fields: {
      ...current.state.fields,
      decision: { paymentDecisionDate: "2026-09-03", payableAmount: "1", payableCurrency: "EUR" },
      payments: [
        {
          paymentId: "10000000-0000-7000-8000-000000000003",
          paymentDate: "2026-09-04",
          paidAmount: "1",
        },
      ],
    },
  },
};

it.each(["en", "lv", "ar"])("submits machine correction modes in %s", async (language) => {
  const user = userEvent.setup();
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language,
      displayLocale: "en-GB",
      mode: "system" as const,
      accent: "green" as const,
    }),
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
  await chooseOption(user, registration, "UPDATE");
  await chooseOption(user, decision, "CLEAR");
  selectFact("claimedAmount");
  const amount = document.querySelector<HTMLInputElement>(
    'input[data-field-name="claimedAmount"]',
  )!;
  await user.clear(amount);
  await user.type(amount, "1.0000");
  for (const next of ["ar", "lv", "en"]) {
    await setEditorLanguage(user, next);
    expect(selectedOption(registration)).toBe("UPDATE");
    expect(selectedOption(decision)).toBe("CLEAR");
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
      registration: { kind: "UPDATE", values: { claimedAmount: "1.0000", claimedCurrency: "EUR" } },
      decision: { kind: "CLEAR" },
    },
  });
});

it("associates a payment amount refusal with its generated field", async () => {
  const user = userEvent.setup();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("operation.preview", "REJECTED", {
          code: "INVALID_INPUT",
          action: "CORRECT_INPUT",
          issues: [{ field: "paidAmount", code: "PAYMENT_AMOUNT_POSITIVE" }],
        }),
      ),
    ),
  );
  const payable: CurrentCase = {
    ...paidCase,
    advice: { ...caseAdvice(["RECORD_PAYMENT"]), history: "FULL" },
    state: {
      ...paidCase.state,
      fields: { ...paidCase.state.fields, payments: [], progress: "DECIDED" },
    },
  };
  render(editor({ initialAct: "RECORD_PAYMENT", current: payable }));
  const amount = screen.getByRole("textbox", { name: "Paid amount" });
  await user.type(amount, "0");
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  await waitFor(() => {
    expect(screen.getByRole("alert")).toHaveFocus();
  });
  expect(amount).toHaveAttribute("aria-invalid", "true");
  expect(amount).toHaveAccessibleDescription(/greater than zero/u);
  await setEditorLanguage(user, "lv");
  expect(document.querySelector('input[data-field-name="paidAmount"]')).toHaveAccessibleDescription(
    /lielāka par nulli/u,
  );
});

it("shows a payment-capacity group issue even without an individual field target", async () => {
  const user = userEvent.setup();
  const fetch = vi.fn().mockResolvedValue(
    response("operation.preview", "REJECTED", {
      code: "INVALID_INPUT",
      action: "CORRECT_INPUT",
      issues: [{ field: "payment", code: "PAYMENT_COUNT_RANGE", maximumPayments: 50 }],
    }),
  );
  vi.stubGlobal("fetch", fetch);
  // The collection can fill after this readable snapshot and before authoritative preview.
  const payable: CurrentCase = {
    ...paidCase,
    advice: { ...caseAdvice(["RECORD_PAYMENT"]), history: "FULL" },
    state: {
      ...paidCase.state,
      fields: { ...paidCase.state.fields, payments: [], progress: "DECIDED" },
    },
  };
  render(editor({ initialAct: "RECORD_PAYMENT", current: payable }));
  await user.type(screen.getByRole("textbox", { name: "Paid amount" }), "1");
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  const summary = await screen.findByRole("alert");
  expect(summary).toHaveTextContent("at most 50 payment entries");
  expect(summary).toHaveTextContent("Remove an entry only if it was recorded in error.");
  expect(summary.querySelectorAll("a")).toHaveLength(0);
  await waitFor(() => {
    expect(summary).toHaveFocus();
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("button", { name: "Record change" })).toBeNull();
});
