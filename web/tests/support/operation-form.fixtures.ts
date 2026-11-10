import type { CurrentCase } from "../../src/protocol/types";
import type { TaskBinding } from "../../src/operation/taskForm";
import { initialOperationValues, assembleOperation } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import { type FormValues } from "../../src/protocol/forms";
import { buildRequest } from "../../src/protocol/client";
import { prepareRequest } from "../../src/protocol/preparedRequest";
export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const current: CurrentCase = {
  screen: { regions: [] },
  context: {
    kind: "CASE",
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 4,
    workRevision: 0,
    history: "FULL",
    tasks: ["CASE:AMEND_REGISTRATION", "CASE:CORRECT", "CASE:CLOSE"],
    editableInputs: [],
    correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP", "UPDATE", "CLEAR"] },
    seeds: { registration: null, decision: null, work: null, paymentCurrency: null },
    notes: [],
    payments: [],
    nextActions: [],
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-09-09T12:00:00.000000Z",
      businessDate: "2026-09-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
export const bindingFor = (kind = "CLOSE"): TaskBinding =>
  kind === "OPEN"
    ? { kind: "NEW_CASE", operationId: firstId }
    : {
        kind: "CASE",
        operationId: firstId,
        target: { kind: "REFERENCE", caseReference: "CASE-SERIALIZATION" },
        revision: current.context.revision,
      };
export const valuesFor = (kind = "CLOSE") => initialOperationValues("CASE", kind, current);
export const requestFor = (values: FormValues, kind = "CLOSE") =>
  buildRequest(
    "operation.execute",
    assembleOperation(operationEntry({ subject: "CASE", kind }), values, bindingFor(kind)).command,
  );
export const preparedFor = (kind = "CLOSE") =>
  prepareRequest("operation.execute", requestFor(valuesFor(kind), kind));
