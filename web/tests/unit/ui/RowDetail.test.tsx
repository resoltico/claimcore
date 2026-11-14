import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Screen, PresentedSection } from "@contracts/client";
import { render, screen } from "../../support/presentation-test-support";
import { endpointResponse } from "../../support/transport-response.fixtures";
import { CaseTimeline } from "../../../src/views/case/CaseTimeline";
import { Table } from "../../../src/ui/regions/Table";
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
  const result = render(<CaseTimeline target={{ kind: "REFERENCE", caseReference: "CASE-1" }} />);
  await screen.findByText("Server event person");
  expect(result.container.querySelector(".row-summary")).toHaveTextContent(
    "Server event person was made responsible",
  );
  const detail = result.container.querySelector("details")!;
  expect(detail).not.toHaveAttribute("open");
  expect(result.container.querySelector(".table-row-detail > .record-view")).toHaveTextContent(
    "9,876.5432 EUR",
  );
  expect(result.container.querySelector(".table-row-detail > .record-view")).not.toHaveTextContent(
    "03/02/2026",
  );
  await user.click(detail.querySelector("summary")!);
  expect(detail).toHaveAttribute("open");
  expect(detail).toHaveTextContent("123.4567 EUR");
  expect(detail).toHaveTextContent("03/02/2026");
  expect(detail).toHaveTextContent("05/04/2026");
  expect(detail.querySelector('[data-entry="old-narrative"] bdi')?.textContent).toBe(
    "Earlier\nexact paragraph",
  );
  expect(detail.querySelector('[data-entry="new-narrative"] bdi')?.textContent).toBe(
    "Replacement\n<script>inert</script>",
  );
  expect(result.container.querySelector("script")).toBeNull();
});
it("keeps supplied historical contact and mixed role scope facts in generic row details", () => {
  const result = render(
    <Table
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
  expect(result.container).toHaveTextContent("past-only@example.test");
  expect(result.container).toHaveTextContent("37");
  expect(result.container.querySelector('[data-entry="installation"]')).toHaveTextContent("Yes");
});

it("retains supplied page facts when the table has no rows", () => {
  const result = render(
    <Table
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
