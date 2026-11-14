import type { OperationTaskId, OperationBinding } from "@contracts/client";
import { operationAffordance } from "./affordances.fixtures";
import type { CurrentCase } from "../../src/protocol/types";

import { initialOperationValues, assembleOperation } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import type { FormValues } from "../../src/protocol/forms";
import { prepareRequest } from "../../src/protocol/preparedRequest";
export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const current: CurrentCase = {
  screen: {
    regions: [],
  },
  subject: {
    kind: "CASE",
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 4,
    workRevision: 0,
  },
  actions: [
    operationAffordance(
      "case.amendRegistration",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 4,
      },
      { "group:registration": "KEEP", "group:decision": "KEEP" },
      [],
      {
        "group:registration": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.registration,
        "group:decision": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.decision,
      },
    ),
    operationAffordance(
      "case.correct",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 4,
      },
      { "group:registration": "KEEP", "group:decision": "KEEP" },
      [],
      {
        "group:registration": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.registration,
        "group:decision": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.decision,
      },
    ),
    operationAffordance(
      "case.close",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 4,
      },
      { "group:registration": "KEEP", "group:decision": "KEEP" },
      [],
      {
        "group:registration": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.registration,
        "group:decision": {
          registration: ["KEEP", "UPDATE"],
          decision: ["KEEP", "UPDATE", "CLEAR"],
        }.decision,
      },
    ),
  ],
  links: [],
  history: "FULL",
  nextCursor: null,
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
export const bindingFor = (kind = "case.close"): OperationBinding =>
  kind === "case.open"
    ? {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "" },
        expectedRevision: 0,
      }
    : {
        kind: "CASE",
        target: {
          kind: "REFERENCE",
          caseReference: "CASE-SERIALIZATION",
        },
        expectedRevision: current.subject.revision,
      };
export const valuesFor = (kind = "case.close") =>
  initialOperationValues(operationAffordance(kind as OperationTaskId, bindingFor(kind)));
export const requestFor = (values: FormValues, kind = "case.close") =>
  assembleOperation(
    operationEntry({
      subject: "CASE",
      kind,
    }),
    values,
    bindingFor(kind),
    firstId,
  ).command;
export const preparedFor = (kind = "case.close") =>
  prepareRequest("operation.execute", requestFor(valuesFor(kind), kind));
