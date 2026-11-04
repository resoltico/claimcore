import { expect, it } from "vitest";
import { render } from "../../../support/presentation-test-support";
import { TimelineChanges } from "../../../../src/views/case/TimelineChanges";
import { actorRefInput } from "../../../support/inputs";
it("renders the supplied typed differences, including each money currency and actor name", () => {
  const view = render(
    <TimelineChanges
      changes={{
        kind: "DISCLOSED",
        items: [
          {
            kind: "CASE_FIELD",
            change: {
              field: "claimantName",
              before: { kind: "PARTY", value: "Core before" },
              after: { kind: "PARTY", value: "Core after" },
            },
          },
          {
            kind: "CASE_MONEY",
            change: {
              field: "payableAmount",
              change: {
                before: { amount: "150000", currency: "JPY" },
                after: { amount: "1234.5678", currency: "EUR" },
              },
            },
          },
          {
            kind: "RESPONSIBILITY",
            change: {
              before: null,
              after: actorRefInput({ displayName: { kind: "NAMED", value: "Named assignee" } }),
            },
          },
          { kind: "LIFECYCLE", change: { before: null, after: "ACTIVE" } },
          {
            kind: "CASE_FIELD",
            change: { field: "status", before: null, after: { kind: "STATUS", value: "OPENED" } },
          },
        ],
      }}
    />,
  );
  expect(view.container.querySelector("del")).toHaveTextContent("Core before");
  expect(view.container.querySelector("ins")).toHaveTextContent("Core after");
  expect(view.container).toHaveTextContent("150,000 JPY");
  expect(view.container).toHaveTextContent("1,234.5678 EUR");
  expect(view.container).toHaveTextContent("Named assignee");
  expect(view.container).toHaveTextContent("Active");
  expect(view.container).toHaveTextContent("Open");
});
it.each(["WITHHELD", "ERASED"] as const)("renders %s without a change panel", (kind) => {
  const view = render(<TimelineChanges changes={{ kind }} />);
  expect(view.container.querySelector("dl")).toBeNull();
});
it("renders core empty changes without inventing a comparison", () => {
  const view = render(<TimelineChanges changes={{ kind: "DISCLOSED", items: [] }} />);
  expect(view.container).toHaveTextContent("No disclosed fields changed.");
});
it("renders reminder differences and work summary independently of fact differences", () => {
  const view = render(
    <TimelineChanges
      changes={{
        kind: "DISCLOSED",
        items: [
          { kind: "HANDLING_SUMMARY", change: { before: null, after: "Core summary" } },
          {
            kind: "REMINDER",
            change: {
              reminderId: "10000000-0000-4000-8000-000000000001",
              action: { before: "Before action", after: "After action" },
              due: { before: null, after: "2026-11-04" },
              state: { before: "PENDING", after: "COMPLETED" },
            },
          },
        ],
      }}
    />,
  );
  expect(view.container).toHaveTextContent("Core summary");
  expect(view.container).toHaveTextContent("After action");
  expect(view.container).toHaveTextContent("Completed");
});
