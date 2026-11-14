import { operationAffordance } from "../../support/affordances.fixtures";
import { describe, expect, it } from "vitest";
import { initialOperationValues, assembleOperation } from "../../../src/operation/operationForm";
import { operationEntry } from "../../../src/protocol/operationCatalog";
import { firstId, bindingFor } from "../../support/operation-form.fixtures";
import { correctionSerializations } from "../../support/correction-serialization.fixtures";
import { expectCorrection } from "../../support/correction-serialization-checks";

const authored = {
  registration: {
    claimantName: "Synthetic replacement",
    claimedAmount: "1.0000",
    claimedCurrency: "EUR",
  },
  decision: { paymentDecisionDate: "2026-09-03", payableAmount: "2.0000", payableCurrency: "EUR" },
};
const correction = (registrationChoice: string, decisionChoice: string) => {
  const values = {
    ...initialOperationValues(
      operationAffordance("case.correct", {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        expectedRevision: 0,
      }),
    ),
    "group:registration": registrationChoice,
    "field:claimantName": authored.registration.claimantName,
    "field:claimedAmount": authored.registration.claimedAmount,
    "field:claimedCurrency": authored.registration.claimedCurrency,
    "group:decision": decisionChoice,
    "field:paymentDecisionDate": authored.decision.paymentDecisionDate,
    "field:payableAmount": authored.decision.payableAmount,
    "field:payableCurrency": authored.decision.payableCurrency,
  };
  return assembleOperation(
    operationEntry({ subject: "CASE", kind: "case.correct" }),
    values,
    bindingFor(),
    firstId,
  ).command;
};

describe("semantic correction assembly", () => {
  it.each(correctionSerializations)(
    "encodes literal %s/%s request",
    (registrationChoice, decisionChoice, expected) => {
      expectCorrection(correction(registrationChoice, decisionChoice), expected, authored);
    },
  );
});

it("correction checks detect changed payloads and retain independent literal expectations", () => {
  const literal = correctionSerializations.find(([r, d]) => r === "UPDATE" && d === "KEEP");
  if (literal === undefined) {
    throw new Error("Independent replacement oracle is missing.");
  }
  const actual = correction("UPDATE", "KEEP");
  if (actual.binding.kind !== "CASE" || actual.task !== "case.correct") {
    throw new Error("Expected correction.");
  }
  const wrong = { ...authored.registration, claimantName: "Wrong authored value" };
  const changed = {
    ...actual,
    inputs: { ...actual.inputs, "field:claimantName": "Wrong authored value" },
  };
  expect(() => {
    expectCorrection(changed, literal[2], authored);
  }).toThrow();
  expect(() => {
    expectCorrection(changed, literal[2], { ...authored, registration: wrong });
  }).toThrow();
  const omitted = JSON.parse(literal[2]) as { inputs: Record<string, unknown> };
  Reflect.deleteProperty(omitted.inputs, "field:claimantName");
  const omissionLiteral = JSON.stringify(omitted);
  expectCorrection(actual, omissionLiteral, authored);
  const missing = { ...actual, inputs: structuredClone(actual.inputs) };
  Reflect.deleteProperty(missing.inputs, "field:claimantName");
  expect(() => {
    expectCorrection(missing, omissionLiteral, authored);
  }).toThrow();
});

it("takes the selected entry from identity while request constants derive from that entry", () => {
  const entry = operationEntry({ subject: "CASE", kind: "case.close" });
  const values = {
    ...initialOperationValues(
      operationAffordance("case.close", {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        expectedRevision: 0,
      }),
    ),
    kind: "PEOPLE",
    "act.kind": "people.disable",
  };
  expect(assembleOperation(entry, values, bindingFor(), firstId).command).toEqual({
    operationId: firstId,
    task: "case.close",
    binding: {
      kind: "CASE",
      target: { kind: "REFERENCE", caseReference: "CASE-SERIALIZATION" },
      expectedRevision: 4,
    },
    inputs: {},
  });
});
