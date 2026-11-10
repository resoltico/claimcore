import { expect, it } from "vitest";
import type { HumanInputIssue } from "@contracts/client";
import { operationTask } from "../../../src/protocol/taskCatalog";
import { projectTaskIssues, taskRowInputKey } from "../../../src/operation/taskIssues";

const issue = (input: HumanInputIssue["input"], ordinal: number | null): HumanInputIssue => ({
  input,
  row: ordinal === null ? null : { collection: "authorityGrants", submittedOrdinal: ordinal },
  message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
  related: null,
});
it("correlates submitted ordinals through the captured map rather than edited row order", () => {
  const rows = Object.freeze({ authorityGrants: Object.freeze(["original-a", "original-b"]) });
  const target = issue("input:role", 1);
  const result = projectTaskIssues([target], operationTask("AUTHORITY:ADMIT"), rows);
  expect(result.fields[taskRowInputKey("authorityGrants", "original-b", "input:role")]).toEqual({
    kind: "humanIssues",
    issues: [target],
  });
  expect(
    result.fields[taskRowInputKey("authorityGrants", "original-a", "input:role")],
  ).toBeUndefined();
  expect(result.unbound).toEqual([]);
});
it("places a collection-level issue on the semantic collection control", () => {
  const target = issue("input:grants", null);
  const result = projectTaskIssues([target], operationTask("AUTHORITY:ADMIT"), {});
  expect(result.fields).toEqual({ authorityGrants: { kind: "humanIssues", issues: [target] } });
  expect(result.unbound).toEqual([]);
});
it("keeps unbound, out-of-range and unexpected row inputs visible instead of dropping or guessing them", () => {
  const targets = [
    issue("input:role", 2),
    issue("input:profile.displayName", 0),
    issue("field:paymentDate", null),
  ];
  const result = projectTaskIssues(targets, operationTask("AUTHORITY:ADMIT"), {
    authorityGrants: ["original-a"],
  });
  expect(result.fields).toEqual({});
  expect(result.unbound).toEqual(targets);
});
