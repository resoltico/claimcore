import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HumanResponseByEndpoint } from "@contracts/client";
import { fireEvent, render, screen } from "../../support/presentation-test-support";
import { LookupInput } from "../../../src/components/LookupInput";
import { taskInput } from "../../../src/protocol/taskCatalog";
const choice = "00000000-0000-4000-8000-000000000002";
const caseId = "00000000-0000-4000-8000-000000000003";
const page: HumanResponseByEndpoint["case.assignees"] = {
  representation: "HUMAN",
  outcome: {
    kind: "SUCCEEDED",
    document: {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caption",
                  labelId: "field.profile.displayName.label",
                  prominence: "PRIMARY",
                  joins: null,
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "irrelevant-row-key",
                  choice: {
                    value: choice,
                    label: {
                      messageId: "label.personChoice",
                      arguments: [
                        { key: "person", value: { kind: "TEXT", value: "Core opaque choice Ψ" } },
                      ],
                    },
                  },
                  values: [{ kind: "TEXT", value: "Table cell" }],
                },
              ],
            },
          },
        ],
      },
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
      basis: {
        kind: "SNAPSHOT",
        observation: {
          observedAt: "2026-11-09T12:00:00.000000Z",
          businessDate: "2026-11-09",
          timeZone: "Etc/UTC",
        },
      },
      disclosure: "CURRENT",
    },
  },
};
afterEach(() => vi.unstubAllGlobals());
const supply = () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(JSON.stringify(page), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      }),
    ),
  );
};

it.each([
  ["case.assignees", "field:responsibleActor"],
  ["case.parties", "input:payee"],
] as const)(
  "renders choices from %s with their supplied labels and values",
  async (lookup, input) => {
    supply();
    const change = vi.fn();
    render(
      <LookupInput
        lookup={lookup}
        caseId={caseId}
        metadata={taskInput(input)}
        inputName="responsible"
        value={choice}
        disabled={false}
        optional
        onChange={change}
      />,
    );
    const selected = screen.getByRole("combobox");
    expect(await screen.findByText("Core opaque choice Ψ")).toBeVisible();
    expect(screen.queryByText(choice)).not.toBeInTheDocument();
    await userEvent.click(selected);
    await userEvent.click(await screen.findByRole("option", { name: "Not recorded" }));
    expect(change).toHaveBeenCalledWith("");
  },
);

it("keeps an unlisted value and omits canonical typing guidance", async () => {
  supply();
  const change = vi.fn();
  const previous = "00000000-0000-4000-8000-000000000004";
  render(
    <LookupInput
      lookup="case.parties"
      caseId={caseId}
      metadata={taskInput("input:payee")}
      inputName="payee"
      value={previous}
      disabled={false}
      onChange={change}
    />,
  );
  const picker = screen.getByRole("combobox", { name: "Payee" });
  expect(await screen.findByText("The current choice is not among those listed.")).toBeVisible();
  expect(picker).toHaveValue(previous);
  fireEvent.focus(picker);
  expect(screen.queryByText("Use a current party's ID.")).not.toBeInTheDocument();
  expect(change).not.toHaveBeenCalled();
});
