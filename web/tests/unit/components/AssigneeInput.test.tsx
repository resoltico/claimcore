import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { isResponseFor, type HumanResponseByEndpoint } from "@contracts/client";
import { render, screen } from "../../support/presentation-test-support";
import { AssigneeInput } from "../../../src/components/AssigneeInput";
import { productMetadata } from "../../../src/protocol/productMetadata";
import type { CurrentCase } from "../../../src/protocol/types";

const saved = "00000000-0000-4000-8000-000000000001";
const choice = "00000000-0000-4000-8000-000000000002";
const current: CurrentCase = {
  screen: {
    regions: [
      {
        role: "WORK",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "responsible",
              labelId: "field.responsibleActor.label",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core saved assignee Ω" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "CASE",
    history: "NONE",
    caseId: "00000000-0000-4000-8000-000000000003",
    revision: 7,
    workRevision: 2,
    tasks: ["work.update"],
    editableInputs: ["field:responsibleActor"],
    correction: null,
    seeds: {
      paymentCurrency: null,
      registration: null,
      decision: null,
      work: { summary: null, responsible: saved },
    },
    notes: [],
    payments: [],
    nextActions: [],
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
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
                  isMoney: false,
                },
              ],
              rows: [{ key: choice, values: [{ kind: "TEXT", value: "Core opaque choice Ψ" }] }],
            },
          },
        ],
      },
      context: { kind: "ASSIGNEES", actorIds: [choice], nextCursor: null },
      basis: current.basis,
      disclosure: "CURRENT",
    },
  },
};
const field = productMetadata.catalog.fields.find(
  (inputField) => inputField.name === "responsibleActor",
)!;
afterEach(() => vi.unstubAllGlobals());
const supply = () => {
  expect(isResponseFor("case.assignees", "HUMAN", page)).toBe(true);
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(JSON.stringify(page), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    ),
  );
};

it("renders saved and offered core captions independently of typed identities and submits the selected handle", async () => {
  supply();
  const change = vi.fn();
  render(
    <AssigneeInput
      current={current}
      field={field}
      inputName="responsible"
      value={saved}
      disabled={false}
      onChange={change}
    />,
  );
  expect(screen.getByText("Core saved assignee Ω")).toBeVisible();
  const radio = await screen.findByRole("radio", { name: "Core opaque choice Ψ" });
  await userEvent.click(radio);
  expect(change).toHaveBeenCalledWith(choice);
  expect(screen.queryByText(saved)).not.toBeInTheDocument();
  expect(screen.queryByText(choice)).not.toBeInTheDocument();
});

it("does not infer the selected caption when its permitted input identity is absent", async () => {
  supply();
  const withoutSeed: CurrentCase = {
    ...current,
    context: {
      ...current.context,
      seeds: { ...current.context.seeds, work: null },
      editableInputs: [],
    },
  };
  render(
    <AssigneeInput
      current={withoutSeed}
      field={field}
      inputName="responsible"
      value={saved}
      disabled={false}
      onChange={vi.fn()}
    />,
  );
  await screen.findByRole("radio", { name: "Core opaque choice Ψ" });
  expect(screen.queryByText("Core saved assignee Ω")).not.toBeInTheDocument();
});
