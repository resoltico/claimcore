import { describe, expect, it } from "vitest";
import type { CurrentCase } from "../src/api/service";
import { createDraft, isCorrectionValues, prefilledValues } from "../src/domain/metadata";
import { caseFields, definition } from "./service-foundation.fixtures";
import { correctionSerializations } from "./correction-serialization.fixtures";

for (const status of ["OPENED", "CLOSED"] as const) {
  describe(`correction serialization while ${status}`, () => {
    it.each(correctionSerializations)(
      "encodes literal %s/%s/%s request [CC-DOM-002]",
      (registration, decision, payment, expected) => {
        const current: CurrentCase = {
          caseReference: "CASE-SERIALIZATION",
          revision: 4,
          state: {
            tag: "LIVE",
            disposition: "ACTIVE",
            fields: { ...caseFields, status },
            availableActs: ["CORRECT_CASE"],
          },
        };
        const values = prefilledValues(definition, "CORRECT_CASE", current);
        if (!isCorrectionValues(values)) {
          throw new Error("Expected grouped correction inputs.");
        }
        values.registration.mode = registration;
        values.registration.values["claimantName"] = "Synthetic replacement";
        values.registration.values["claimedAmount"] = "1.0000";
        values.decision = {
          mode: decision,
          values: {
            paymentDecisionDate: "2026-09-03",
            payableAmount: "2.0000",
            payableCurrency: "EUR",
          },
        };
        values.payment = { mode: payment, values: { paymentDate: "2026-09-04" } };
        const request = createDraft(
          "00000000-0000-4000-8000-000000000001",
          "CASE-SERIALIZATION",
          current.revision,
          "CORRECT_CASE",
          values,
        );
        expect(JSON.stringify(request)).toBe(expected);
      },
    );
  });
}
