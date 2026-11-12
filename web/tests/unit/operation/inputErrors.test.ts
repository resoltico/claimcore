import { expect, it } from "vitest";
import { inputErrors } from "../../../src/operation/inputErrors";
import { operationTask } from "../../../src/protocol/taskCatalog";
import type { HumanRejection } from "@contracts/client";
import type { Notice } from "../../../src/protocol/notices";

it("maps supplied HUMAN input identities in their original order without reconstructing DATA issue paths", () => {
  const rejection: HumanRejection = {
    kind: "INPUT",
    advice: {
      message: {
        messageId: "diagnostic.INVALID_INPUT",
        arguments: [{ key: "issueCount", value: { kind: "COUNT", value: 2 } }],
      },
      action: "CORRECT_INPUT",
    },
    issues: [
      {
        input: "field:incidentDate",
        row: null,
        related: null,
        message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
      },
      {
        input: "field:claimantName",
        row: null,
        related: null,
        message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
      },
    ],
  };
  const errors = inputErrors(rejection, operationTask("CASE:OPEN"), {}, null, {
    kind: "local",
    reason: "invalidInput",
  });
  expect(Object.keys(errors)).toEqual(["field:incidentDate", "field:claimantName"]);
  expect(errors["field:incidentDate"]).toEqual({
    kind: "humanIssues",
    issues: [rejection.issues[0]],
  });
  expect(errors["field:claimantName"]).toEqual({
    kind: "humanIssues",
    issues: [rejection.issues[1]],
  });
});

it("keeps a protocol fault single without deriving a business issue", () => {
  const notice: Notice = { kind: "local", reason: "invalidInput" };
  expect(
    inputErrors(undefined, operationTask("CASE:OPEN"), {}, "field:incidentDate", notice),
  ).toEqual({ "field:incidentDate": notice });
  expect(inputErrors(undefined, operationTask("CASE:OPEN"), {}, null, notice)).toEqual({});
});
