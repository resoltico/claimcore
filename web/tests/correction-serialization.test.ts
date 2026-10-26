import { describe, expect, it } from "vitest";
import { initialOperationValues, assembleOperation } from "../src/operation/operationForm";
import { current, firstId } from "./operation-form.fixtures";
import { correctionSerializations } from "./correction-serialization.fixtures";
for (const status of ["OPENED", "CLOSED"] as const) {
  describe(`correction serialization while ${status}`, () => {
    it.each(correctionSerializations)(
      "encodes literal %s/%s/%s request",
      (registration, decision, payment, expected) => {
        const values = {
          ...initialOperationValues(
            "CASE",
            "CORRECT",
            {
              ...current,
              state: {
                ...current.state,
                fields: {
                  ...current.state.fields,
                  status,
                },
              },
            },
            firstId,
          ),
          "act.groups.registration.kind": registration,
          "act.groups.registration.values.claimantName": "Synthetic replacement",
          "act.groups.registration.values.claimedAmount": "1.0000",
          "act.groups.decision.kind": decision,
          "act.groups.decision.values.paymentDecisionDate": "2026-09-03",
          "act.groups.decision.values.payableAmount": "2.0000",
          "act.groups.decision.values.payableCurrency": "EUR",
          "act.groups.payment.kind": payment,
          "act.groups.payment.values.paymentDate": "2026-09-04",
        };
        expect(assembleOperation(values)).toEqual({
          kind: "CASE",
          ...JSON.parse(expected),
        });
      },
    );
  });
}
