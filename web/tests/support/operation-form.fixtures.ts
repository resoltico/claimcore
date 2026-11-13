import type { CurrentCase } from "../../src/protocol/types";
import type { TaskBinding } from "../../src/operation/taskForm";
import { initialOperationValues, assembleOperation } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import { type FormValues } from "../../src/protocol/forms";
import { prepareRequest } from "../../src/protocol/preparedRequest";
export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const current: CurrentCase = {
  screen: {
    regions: [],
  },
  context: {
    kind: "CASE",
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 4,
    workRevision: 0,
    history: "FULL",
    tasks: ["case.amendRegistration", "case.correct", "case.close"],
    editableInputs: [],
    correction: {
      registration: ["KEEP", "UPDATE"],
      decision: ["KEEP", "UPDATE", "CLEAR"],
    },
    seeds: {
      registration: null,
      decision: null,
      work: null,
      paymentCurrency: null,
    },
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
export const bindingFor = (kind = "case.close"): TaskBinding =>
  kind === "case.open"
    ? {
        kind: "NEW_CASE",
        operationId: firstId,
      }
    : {
        kind: "CASE",
        operationId: firstId,
        target: {
          kind: "REFERENCE",
          caseReference: "CASE-SERIALIZATION",
        },
        revision: current.context.revision,
      };
export const valuesFor = (kind = "case.close") => initialOperationValues("CASE", kind, current);
export const requestFor = (values: FormValues, kind = "case.close") =>
  assembleOperation(
    operationEntry({
      subject: "CASE",
      kind,
    }),
    values,
    bindingFor(kind),
  ).command;
export const preparedFor = (kind = "case.close") =>
  prepareRequest("operation.execute", requestFor(valuesFor(kind), kind));
