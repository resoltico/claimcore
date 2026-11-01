import { caseViewInput } from "./inputs";
import type { CurrentCase } from "../../src/protocol/types";
import { caseFields } from "./service-foundation.fixtures";
import { caseAdvice } from "./case-advice.fixtures";
import { initialOperationValues, assembleOperation } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import { type FormValues } from "../../src/protocol/forms";
import { buildRequest } from "../../src/protocol/client";
import { prepareRequest } from "../../src/protocol/preparedRequest";
export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const current: CurrentCase = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: "CASE-SERIALIZATION",
  revision: 4,
  advice: {
    ...caseAdvice(["AMEND_REGISTRATION", "CORRECT", "CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields: {
      ...caseFields,
      caseReference: "CASE-SERIALIZATION",
    },
  },
});
export const valuesFor = (kind = "CLOSE") => initialOperationValues("CASE", kind, current, firstId);
export const requestFor = (values: FormValues, kind = "CLOSE") =>
  buildRequest(
    "operation.execute",
    assembleOperation(operationEntry({ subject: "CASE", kind }), values),
  );
export const preparedFor = (kind = "CLOSE") =>
  prepareRequest("operation.execute", requestFor(valuesFor(kind), kind));
