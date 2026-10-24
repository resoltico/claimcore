import { describe, expect, it } from "vitest";
import { initialOperationValues, operationDocument } from "../src/interaction/operationForm";
import { formValue } from "../src/protocol/forms";
import { current, firstId } from "./operation-form.fixtures";
import { correctionSerializations } from "./correction-serialization.fixtures";
for (const status of ["OPENED", "CLOSED"] as const) {
  describe(`correction serialization while ${status}`, () => {
    it.each(correctionSerializations)(
      "encodes literal %s/%s/%s request [CC-DOM-002]",
      (registration, decision, payment, expected) => {
        const values = {
          ...initialOperationValues(
            "CASE",
            "CORRECT_CASE",
            {
              ...current,
              state: { ...current.state, fields: { ...current.state.fields, status } },
            },
            firstId,
          ),
          "act.groups.registration.mode": registration,
          "act.groups.registration.values.claimantName": "Synthetic replacement",
          "act.groups.registration.values.claimedAmount": "1.0000",
          "act.groups.decision.mode": decision,
          "act.groups.decision.values.paymentDecisionDate": "2026-09-03",
          "act.groups.decision.values.payableAmount": "2.0000",
          "act.groups.decision.values.payableCurrency": "EUR",
          "act.groups.payment.mode": payment,
          "act.groups.payment.values.paymentDate": "2026-09-04",
        };
        expect(formValue(operationDocument, operationDocument, "", values)).toEqual({
          scope: "CASE",
          ...JSON.parse(expected),
        });
      },
    );
  });
}
