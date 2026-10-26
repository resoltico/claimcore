import type { CurrentCase } from "../src/protocol/types";
import { caseFields } from "./service-foundation.fixtures";
import { caseOffers } from "./case-advice.fixtures";
import { initialOperationValues, assembleOperation } from "../src/operation/operationForm";
import { type FormValues } from "../src/protocol/forms";
import { buildRequest } from "../src/protocol/client";
import { prepareRequest } from "../src/protocol/preparedRequest";
export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const current: CurrentCase = {
  caseReference: "CASE-SERIALIZATION",
  revision: 4,
  advice: {
    acts: caseOffers(["AMEND_REGISTRATION", "CORRECT", "CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields: {
      ...caseFields,
      caseReference: "CASE-SERIALIZATION",
    },
  },
};
export const valuesFor = (kind = "CLOSE") => initialOperationValues("CASE", kind, current, firstId);
export const requestFor = (values: FormValues) =>
  buildRequest("operation.execute", assembleOperation(values));
export const preparedFor = (kind = "CLOSE") =>
  prepareRequest("operation.execute", requestFor(valuesFor(kind)));
