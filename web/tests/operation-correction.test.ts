import { expect, it } from "vitest";
import { commandInputs, createDraft, prefilledValues } from "../src/interaction/metadata";
import { definition } from "./service-foundation.fixtures";

const operationId = "00000000-0000-4000-8000-000000000001";

it("encodes every correction action explicitly and refuses an invalid registration clear", () => {
  const values = {
    registration: { mode: "KEEP" as const, values: {} },
    decision: { mode: "REPLACE" as const, values: {} },
    payment: { mode: "REPLACE" as const, values: {} },
  };
  const request = createDraft(operationId, "CASE-SYNTHETIC", 1, "CORRECT_CASE", values);
  if (request.act.kind !== "CORRECT_CASE") {
    throw new Error("Expected a correction request.");
  }
  expect(request.act.groups).toEqual({
    registration: { mode: "KEEP" },
    decision: {
      mode: "REPLACE",
      values: { paymentDecisionDate: "", payableAmount: "", payableCurrency: "" },
    },
    payment: { mode: "REPLACE", values: { paymentDate: "" } },
  });
  expect(() =>
    createDraft(operationId, "CASE-SYNTHETIC", 1, "CORRECT_CASE", {
      ...values,
      registration: { mode: "CLEAR", values: {} },
    }),
  ).toThrow("Unsupported registration correction choice.");
});

it("rejects impossible local draft shapes and keeps absent current correction values blank", () => {
  expect(prefilledValues(definition, "AMEND_REGISTRATION", null)).toMatchObject({
    claimantName: "",
    incidentDate: "",
  });
  expect(() => {
    Reflect.apply(commandInputs, undefined, [{ ...definition, fields: [] }, "OPEN"]);
  }).toThrow("did not describe");
  expect(() => createDraft(operationId, "CASE-SYNTHETIC", 1, "CORRECT_CASE", {})).toThrow(
    "CORRECT_CASE requires grouped values.",
  );
  expect(() =>
    createDraft(operationId, "CASE-SYNTHETIC", 1, "OPEN", {
      registration: { mode: "KEEP", values: {} },
      decision: { mode: "KEEP", values: {} },
      payment: { mode: "KEEP", values: {} },
    }),
  ).toThrow("OPEN requires flat values.");
});

it("retains authored replacement values for every grouped correction field", () => {
  const request = createDraft(operationId, "CASE-SYNTHETIC", 1, "CORRECT_CASE", {
    registration: {
      mode: "REPLACE",
      values: {
        incidentDate: "2026-09-01",
        incidentNotificationDate: "2026-09-02",
        incidentCountry: "Latvia",
        claimantName: "Synthetic",
        insurerName: "Synthetic insurer",
        claimedAmount: "1.00",
        claimedCurrency: "EUR",
      },
    },
    decision: {
      mode: "REPLACE",
      values: { paymentDecisionDate: "2026-09-03", payableAmount: "2.00", payableCurrency: "EUR" },
    },
    payment: { mode: "REPLACE", values: { paymentDate: "2026-09-04" } },
  });
  if (request.act.kind !== "CORRECT_CASE") {
    throw new Error("Expected a correction request.");
  }
  expect(request.act.groups.decision).toEqual({
    mode: "REPLACE",
    values: { paymentDecisionDate: "2026-09-03", payableAmount: "2.00", payableCurrency: "EUR" },
  });
  expect(request.act.groups.payment).toEqual({
    mode: "REPLACE",
    values: { paymentDate: "2026-09-04" },
  });
});
