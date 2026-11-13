import { describe, expect, it } from "vitest";
import { initialOperationValues, assembleOperation } from "../../../src/operation/operationForm";
import { operationEntry } from "../../../src/protocol/operationCatalog";
import { current, firstId, bindingFor } from "../../support/operation-form.fixtures";
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
    ...initialOperationValues("CASE", "case.correct", current),
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
  if (actual.kind !== "CASE" || actual.act.kind !== "case.correct") {
    throw new Error("Expected correction.");
  }
  const wrong = { ...authored.registration, claimantName: "Wrong authored value" };
  const changed = {
    ...actual,
    act: {
      ...actual.act,
      groups: { ...actual.act.groups, registration: { kind: "UPDATE" as const, values: wrong } },
    },
  };
  expect(() => {
    expectCorrection(changed, literal[2], authored);
  }).toThrow();
  expect(() => {
    expectCorrection(changed, literal[2], { ...authored, registration: wrong });
  }).toThrow();
  const omitted = JSON.parse(literal[2]) as {
    act: { groups: { registration: { values: Record<string, string> } } };
  };
  Reflect.deleteProperty(omitted.act.groups.registration.values, "claimantName");
  const omissionLiteral = JSON.stringify(omitted);
  expectCorrection(actual, omissionLiteral, authored);
  const missing = { ...actual, act: structuredClone(actual.act) };
  if (missing.act.groups.registration.kind !== "UPDATE") {
    throw new Error("Expected replacement.");
  }
  Reflect.deleteProperty(missing.act.groups.registration.values, "claimantName");
  expect(() => {
    expectCorrection(missing, omissionLiteral, authored);
  }).toThrow();
});

it("takes the selected entry from identity while request constants derive from that entry", () => {
  const entry = operationEntry({ subject: "CASE", kind: "case.close" });
  const values = {
    ...initialOperationValues("CASE", "case.close", current),
    kind: "PEOPLE",
    "act.kind": "people.disable",
  };
  expect(assembleOperation(entry, values, bindingFor()).command).toEqual({
    kind: "CASE",
    operationId: firstId,
    target: { kind: "REFERENCE", caseReference: "CASE-SERIALIZATION" },
    expectedRevision: 4,
    act: { kind: "case.close" },
  });
});
