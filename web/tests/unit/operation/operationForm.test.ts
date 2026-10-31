import { describe, expect, it } from "vitest";
import {
  initialOperationValues,
  assembleOperation,
  operationEntry,
} from "../../../src/operation/operationForm";
import { current, firstId } from "../../support/operation-form.fixtures";
import { correctionSerializations } from "../../support/correction-serialization.fixtures";
import { registration, decision, payment } from "../../support/inputs";
import { expectCorrection } from "../../support/correction-serialization-checks";

const authored = {
  registration: registration({ claimantName: "Synthetic replacement", claimedAmount: "1.0000" }),
  decision: decision({
    paymentDecisionDate: "2026-09-03",
    payableAmount: "2.0000",
    payableCurrency: "EUR",
  }),
  payment: payment({ paymentDate: "2026-09-04" }),
};
const correction = (
  status: "OPENED" | "CLOSED",
  registrationChoice: string,
  decisionChoice: string,
  paymentChoice: string,
) => {
  const values = {
    ...initialOperationValues(
      "CASE",
      "CORRECT",
      {
        ...current,
        state: { ...current.state, fields: { ...current.state.fields, status } },
      },
      firstId,
    ),
    "act.groups.registration.kind": registrationChoice,
    "act.groups.registration.values.claimantName": authored.registration.claimantName,
    "act.groups.registration.values.claimedAmount": authored.registration.claimedAmount,
    "act.groups.decision.kind": decisionChoice,
    "act.groups.decision.values.paymentDecisionDate": authored.decision.paymentDecisionDate,
    "act.groups.decision.values.payableAmount": authored.decision.payableAmount,
    "act.groups.decision.values.payableCurrency": authored.decision.payableCurrency,
    "act.groups.payment.kind": paymentChoice,
    "act.groups.payment.values.paymentDate": authored.payment.paymentDate,
  };
  return assembleOperation(operationEntry({ subject: "CASE", kind: "CORRECT" }), values);
};
for (const status of ["OPENED", "CLOSED"] as const) {
  describe(`correction serialization while ${status}`, () => {
    it.each(correctionSerializations)(
      "encodes literal %s/%s/%s request",
      (registrationChoice, decisionChoice, paymentChoice, expected) => {
        expectCorrection(
          correction(status, registrationChoice, decisionChoice, paymentChoice),
          expected,
          authored,
        );
      },
    );
  });
}

it("correction checks detect changed payloads and retain independent literal expectations", () => {
  const literal = correctionSerializations.find(
    ([r, d, p]) => r === "REPLACE" && d === "KEEP" && p === "KEEP",
  );
  if (literal === undefined) {
    throw new Error("Independent replacement oracle is missing.");
  }
  const actual = correction("OPENED", "REPLACE", "KEEP", "KEEP");
  if (actual.kind !== "CASE" || actual.act.kind !== "CORRECT") {
    throw new Error("Expected correction.");
  }
  const wrong = { ...authored.registration, claimantName: "Wrong authored value" };
  const changed = {
    ...actual,
    act: {
      ...actual.act,
      groups: { ...actual.act.groups, registration: { kind: "REPLACE" as const, values: wrong } },
    },
  };
  expect(() => {
    expectCorrection(changed, literal[3], authored);
  }).toThrow();
  expect(() => {
    expectCorrection(changed, literal[3], { ...authored, registration: wrong });
  }).toThrow();
  const omitted = JSON.parse(literal[3]) as {
    act: { groups: { registration: { values: Record<string, string> } } };
  };
  Reflect.deleteProperty(omitted.act.groups.registration.values, "claimantName");
  const omissionLiteral = JSON.stringify(omitted);
  expectCorrection(actual, omissionLiteral, authored);
  const missing = { ...actual, act: structuredClone(actual.act) };
  if (missing.act.groups.registration.kind !== "REPLACE") {
    throw new Error("Expected replacement.");
  }
  Reflect.deleteProperty(missing.act.groups.registration.values, "claimantName");
  expect(() => {
    expectCorrection(missing, omissionLiteral, authored);
  }).toThrow();
});

it("takes the selected entry from identity while request constants derive from that entry", () => {
  const entry = operationEntry({ subject: "CASE", kind: "CLOSE" });
  const values = {
    ...initialOperationValues("CASE", "CLOSE", current, firstId),
    kind: "AUTHORITY",
    "act.kind": "DISABLE",
  };
  expect(assembleOperation(entry, values)).toEqual({
    kind: "CASE",
    operationId: firstId,
    target: { kind: "REFERENCE", caseReference: current.caseReference },
    expectedRevision: current.revision,
    act: { kind: "CLOSE" },
  });
});
