import { SuppliedMessage } from "../../../src/ui/SuppliedMessage";
import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { NoticeView } from "../../../src/presentation/Message";
import type { HumanInputIssue } from "@contracts/client";

const show = (
  message: HumanInputIssue["message"],
  input: HumanInputIssue["input"] = "field:incidentDate",
) =>
  render(
    <NoticeView
      value={{ kind: "humanIssues", issues: [{ input, row: null, related: null, message }] }}
    />,
  );

it("uses the Core-declared widget wording for a supplied calendar issue", () => {
  show({ messageId: "diagnostic.CALENDAR_DATE_REQUIRED", arguments: [] });
  expect(screen.getByText("Enter a real date.")).toBeVisible();
  expect(screen.queryByText(/YYYY/u)).not.toBeInTheDocument();
});

it("preserves an independently supplied message identity without inferring wording from its input", () => {
  show({ messageId: "label.recorded", arguments: [] }, "field:claimantName");
  expect(screen.getByText("Recorded.")).toBeVisible();
});

it("retains supplied typed arguments and isolation when no widget variant is declared", () => {
  const { container } = show(
    {
      messageId: "diagnostic.TOO_LONG",
      arguments: [{ key: "maximumCharacters", value: { kind: "COUNT", value: 200 } }],
    },
    "field:claimantName",
  );
  expect(container).toHaveTextContent("Use at most 200 characters.");
  expect(container.querySelector("bdi")).toHaveTextContent("200");
});

it("keeps the supplied primary wording outside widget issues", () => {
  render(
    <SuppliedMessage
      message={{ messageId: "diagnostic.CALENDAR_DATE_REQUIRED", arguments: [] }}
      timeZone="UTC"
    />,
  );
  expect(screen.getByText("Use one valid calendar date in YYYY-MM-DD format.")).toBeVisible();
});
