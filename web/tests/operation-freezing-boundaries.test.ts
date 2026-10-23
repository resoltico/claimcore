import { expect, it } from "vitest";
import { prepareRequest } from "../src/protocol/preparedRequest";
import { valuesFor, requestFor } from "./operation-form.fixtures";
it("detaches replacement values and preserves decimal spelling after authoring changes", () => {
  const values = {
    ...valuesFor("CORRECT_CASE"),
    "act.groups.registration.mode": "REPLACE",
    "act.groups.registration.values.claimantName": "Before",
    "act.groups.registration.values.claimedAmount": "1.0000",
  };
  const request = requestFor(values);
  const prepared = prepareRequest("operation.execute", request);
  Object.assign(values, { "act.groups.registration.values.claimantName": "After" });
  if (
    request.scope !== "CASE" ||
    request.act.kind !== "CORRECT_CASE" ||
    request.act.groups.registration.mode !== "REPLACE"
  ) {
    throw new Error("Missing correction fixture.");
  }
  Object.assign(request.act.groups.registration.values, { claimantName: "Changed" });
  expect(prepared.bytes).toContain('"claimantName":"Before"');
  expect(prepared.bytes).toContain('"claimedAmount":"1.0000"');
  expect(prepared.input).not.toBe(request);
});
