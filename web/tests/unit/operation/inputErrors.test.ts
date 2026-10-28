import { expect, it } from "vitest";
import { inputErrors } from "../../../src/operation/inputErrors";
import { initialOperationValues } from "../../../src/operation/operationForm";
import type { Notice } from "../../../src/protocol/notices";

it("maps every issue through catalog diagnostics in their original order", () => {
  const notice: Notice = {
    kind: "diagnostic",
    diagnostic: {
      code: "INVALID_INPUT",
      action: "CORRECT_INPUT",
      issues: [
        { field: "incidentDate", code: "TEXT_REQUIRED" },
        { field: "claimantName", code: "TEXT_REQUIRED" },
      ],
    },
  };
  const values = initialOperationValues("CASE", "OPEN", null);
  const errors = inputErrors(notice, null, values);
  expect(Object.keys(errors)).toEqual(["act.values.incidentDate", "act.values.claimantName"]);
  expect(errors["act.values.incidentDate"]).toEqual({
    kind: "issues",
    issues: [{ field: "incidentDate", code: "TEXT_REQUIRED" }],
  });
  expect(errors["act.values.claimantName"]).toEqual({
    kind: "issues",
    issues: [{ field: "claimantName", code: "TEXT_REQUIRED" }],
  });
});

it("keeps a protocol fault single without deriving a business issue", () => {
  const notice: Notice = { kind: "local", reason: "invalidInput" };
  const values = initialOperationValues("CASE", "OPEN", null);
  expect(inputErrors(notice, "act.values.incidentDate", values)).toEqual({
    "act.values.incidentDate": notice,
  });
});
