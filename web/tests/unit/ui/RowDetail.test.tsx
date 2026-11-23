import { within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Screen, PresentedSection } from "@contracts/client";
import { render, screen } from "../../support/presentation-test-support";
import { endpointResponse } from "../../support/transport-response.fixtures";
import { CaseTimeline } from "../../../src/views/case/CaseTimeline";
import { DataTable } from "../../../src/ui/DataTable";
import { actorRefInput, workBasis } from "../../support/inputs";

const differences: ReadonlyArray<PresentedSection> = [
  {
    titleId: "field.claimedAmount.label",
    entries: [
      {
        key: "old",
        labelId: "label.before",
        prominence: "PRIMARY",
        value: { kind: "MONEY", amount: "9876.5432", currency: "EUR", displayScale: 4 },
      },
      {
        key: "new",
        labelId: "label.after",
        prominence: "PRIMARY",
        value: { kind: "MONEY", amount: "123.4567", currency: "EUR", displayScale: 4 },
      },
    ],
  },
  {
    titleId: "field.incidentDate.label",
    entries: [
      {
        key: "old-date",
        labelId: "label.before",
        prominence: "PRIMARY",
        value: { kind: "DATE", value: "2026-02-03" },
      },
      {
        key: "new-date",
        labelId: "label.after",
        prominence: "PRIMARY",
        value: { kind: "DATE", value: "2026-04-05" },
      },
      {
        key: "old-narrative",
        labelId: "label.before",
        prominence: "PRIMARY",
        value: { kind: "NARRATIVE", value: "Earlier\nexact paragraph" },
      },
      {
        key: "new-narrative",
        labelId: "label.after",
        prominence: "PRIMARY",
        value: { kind: "NARRATIVE", value: "Replacement\n<script>inert</script>" },
      },
    ],
  },
];
const supplied: Screen = {
  regions: [
    {
      role: "RESULTS",
      content: {
        kind: "TABLE",
        sections: [],
        columns: [
          {
            key: "arbitrary-key",
            labelId: "label.sequence",
            prominence: "PRIMARY",
            isMoney: false,
          },
        ],
        rows: ([[{ kind: "COUNT", value: 19 }]] as const).map((values, index) => ({
          key: String(index),
          values,
          detail: [
            {
              summary: {
                messageId: "timeline.event.responsibilityAssigned",
                arguments: [
                  {
                    key: "person",
                    value: {
                      kind: "ACTOR" as const,
                      value: actorRefInput({
                        displayName: { kind: "NAMED", value: "Server event person" },
                      }),
                    },
                  },
                ],
              },
              highlights: [differences[0]!],
              sections: differences,
            },
          ][index]!,
        })),
      },
    },
  ],
};
const suppliedTable = supplied.regions[0]!.content;
if (suppliedTable.kind !== "TABLE") {
  throw Error("Expected supplied table");
}

it("renders exact endpoint-supplied details and typed event arguments without a business DTO", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      endpointResponse("case.timeline", {
        representation: "HUMAN",
        outcome: {
          kind: "SUCCEEDED",
          document: {
            screen: supplied,
            subject: { kind: "LIST" },
            actions: [],
            links: [],
            history: null,
            nextCursor: null,
            basis: { kind: "SNAPSHOT", observation: workBasis },
            disclosure: "FULL",
          },
        },
      }),
    ),
  );
  const user = userEvent.setup();
  render(<CaseTimeline target={{ kind: "REFERENCE", caseReference: "CASE-1" }} />);
  await user.click(await screen.findByRole("button", { name: "Actions for Results" }));
  await user.click(screen.getByRole("menuitem", { name: "View details" }));
  const detail = screen.getByRole("dialog", { name: "View details" });
  expect(detail).toHaveTextContent("Server event person was made responsible");
  expect(detail).toHaveTextContent("9,876.5432 EUR");
  expect(detail).toHaveTextContent("123.4567 EUR");
  expect(detail).toHaveTextContent("03/02/2026");
  expect(detail).toHaveTextContent("05/04/2026");
  expect(detail.querySelector('[data-entry="old-narrative"] bdi')?.textContent).toBe(
    "Earlier\nexact paragraph",
  );
  expect(detail.querySelector('[data-entry="new-narrative"] bdi')?.textContent).toBe(
    "Replacement\n<script>inert</script>",
  );
  expect(detail.querySelector("script")).toBeNull();
});
it("keeps supplied historical contact and mixed role scope facts in generic row details", async () => {
  render(
    <DataTable
      view={{
        ...suppliedTable,
        rows: suppliedTable.rows.map((row) => ({
          key: row.key,
          values: row.values,
          detail: (
            [
              {
                summary: null,
                highlights: [],
                sections: [
                  {
                    titleId: "label.account",
                    entries: [
                      {
                        key: "historical-contact",
                        labelId: "field.profile.contact.label",
                        prominence: "PRIMARY",
                        value: { kind: "TEXT", value: "past-only@example.test" },
                      },
                      {
                        key: "installation",
                        labelId: "label.installationRoleAssigned",
                        prominence: "PRIMARY",
                        value: { kind: "CODE", vocabulary: "boolean", token: "TRUE" },
                      },
                      {
                        key: "cases",
                        labelId: "label.caseRoleAssignments",
                        prominence: "PRIMARY",
                        value: { kind: "COUNT", value: 37 },
                      },
                    ],
                  },
                ],
              },
            ] as const
          )[0],
        })),
      }}
    />,
  );
  const user = userEvent.setup();
  expect(screen.queryByText("past-only@example.test")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Actions for Results" }));
  await user.click(screen.getByRole("menuitem", { name: "View details" }));
  const detail = screen.getByRole("dialog", { name: "View details" });
  expect(within(detail).getByText("past-only@example.test")).toBeVisible();
  expect(detail).toHaveTextContent("37");
  expect(detail.querySelector('[data-entry="installation"]')).toHaveTextContent("Yes");
});

it("retains supplied page facts when the table has no rows", () => {
  const result = render(
    <DataTable
      view={{
        kind: "TABLE",
        columns: [],
        sections: [
          {
            titleId: "label.account",
            entries: [
              {
                key: "empty-page-contact",
                labelId: "field.profile.contact.label",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "empty-table-page@example.test" },
              },
            ],
          },
        ],
        rows: ([] as const).map((values, index) => ({
          key: String(index),
          values,
        })),
      }}
    />,
  );
  expect(result.container.querySelector('[data-entry="empty-page-contact"]')).toHaveTextContent(
    "empty-table-page@example.test",
  );
  expect(result.container.querySelectorAll("tbody > tr")).toHaveLength(0);
});
