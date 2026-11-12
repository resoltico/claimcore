import {
  caseDocumentInput,
  registrationInputs,
  decisionInputs,
  correctionChoices,
  emptyCaseSeeds,
} from "../support/case-document.fixtures";
import { selectFact } from "../support/field-entry";
import { chooseOption, selectedOption } from "../support/choice-entry";

import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { editor, draftAt, setEditorLanguage } from "../support/presentation-state.fixtures";
import type { CurrentCase } from "../../src/api/service";
import { fields } from "../support/service-ui.fixtures";

import { endpointResponse } from "../support/transport-response.fixtures";
import { preferenceKey } from "../../src/presentation/preferences";

const decision = { paymentDecisionDate: "2026-09-03", payableAmount: "1", payableCurrency: "EUR" };
const paidCase: CurrentCase = caseDocumentInput({
  tasks: ["CASE:CORRECT"],
  revision: 1,
  history: "FULL",
  editableInputs: [...registrationInputs, ...decisionInputs],
  correction: correctionChoices,
  seeds: { ...emptyCaseSeeds, registration: fields.registration, decision },
});
const payable: CurrentCase = caseDocumentInput({
  tasks: ["CASE:RECORD_PAYMENT"],
  revision: 1,
  history: "FULL",
  seeds: { ...emptyCaseSeeds, paymentCurrency: decision?.payableCurrency ?? null },
});
const inputRefusal = (
  input: "field:paidAmount" | "group:payment",
  messageId: "diagnostic.PAYMENT_AMOUNT_POSITIVE" | "diagnostic.PAYMENT_COUNT_LIMIT",
) =>
  endpointResponse("operation.preview", {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "INPUT",
        advice: {
          message: {
            messageId: "diagnostic.INVALID_INPUT",
            arguments: [{ key: "issueCount", value: { kind: "COUNT", value: 1 } }],
          },
          action: "CORRECT_INPUT",
        },
        issues: [{ input, row: null, related: null, message: { messageId, arguments: [] } }],
      },
    },
  });

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
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockRejectedValue(new Error("Synthetic lost response")),
  );
  render(
    editor({
      initialAct: "CORRECT",
      current: paidCase,
    }),
  );
  const registration = document.querySelector<HTMLSelectElement>(
    'fieldset[id="field-group:registration"]',
  )!;
  const decisionControls = document.querySelector<HTMLSelectElement>(
    'fieldset[id="field-group:decision"]',
  )!;
  await chooseOption(user, registration, "UPDATE");
  await chooseOption(user, decisionControls, "CLEAR");
  selectFact("claimedAmount");
  const amount = document.querySelector<HTMLInputElement>(
    'input[data-field-name="claimedAmount"]',
  )!;
  await user.clear(amount);
  await user.type(amount, "1.0000");
  for (const next of ["ar", "lv", "en"]) {
    await setEditorLanguage(user, next);
    expect(selectedOption(registration)).toBe("UPDATE");
    expect(selectedOption(decisionControls)).toBe("CLEAR");
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
    vi
      .fn()
      .mockImplementation(() =>
        Promise.resolve(inputRefusal("field:paidAmount", "diagnostic.PAYMENT_AMOUNT_POSITIVE")),
      ),
  );
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

it("shows the exact-integer payment limit as a group issue without inventing a field target", async () => {
  const user = userEvent.setup();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(inputRefusal("group:payment", "diagnostic.PAYMENT_COUNT_LIMIT"));
  vi.stubGlobal("fetch", fetch);
  // The collection can fill after this readable snapshot and before authoritative preview.
  render(editor({ initialAct: "RECORD_PAYMENT", current: payable }));
  await user.type(screen.getByRole("textbox", { name: "Paid amount" }), "1");
  await user.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);
  const summary = await screen.findByRole("alert");
  expect(summary).toHaveTextContent("supported exact-integer limit");
  expect(summary).not.toHaveTextContent("50 payment entries");
  expect(summary.querySelectorAll("a")).toHaveLength(0);
  await waitFor(() => {
    expect(summary).toHaveFocus();
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("button", { name: "Record change" })).toBeNull();
});
