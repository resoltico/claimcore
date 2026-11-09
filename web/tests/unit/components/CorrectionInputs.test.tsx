import { expect, it } from "vitest";
import userEvent from "@testing-library/user-event";
import { isResponseFor } from "@contracts/client";
import { render, screen } from "../../support/presentation-test-support";
import { CorrectionInputs } from "../../../src/components/CorrectionInputs";
import type { CurrentCase } from "../../../src/protocol/types";

const current: CurrentCase = {
  screen: {
    regions: [
      {
        role: "FACTS",
        titleId: "group.registration.label",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "claimantName",
              labelId: "field.claimantName.label",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Other read-only facts Ψ" },
            },
          ],
        },
      },
    ],
    currentGroups: [
      {
        group: "registration",
        section: {
          titleId: "label.details",
          entries: [
            {
              key: "opaque-before",
              labelId: "field.claimantName.label",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core saved group caption Ω" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "CASE",
    caseId: "00000000-0000-4000-8000-000000000001",
    revision: 7,
    workRevision: 0,
    history: "NONE",
    tasks: ["CASE:CORRECT"],
    editableInputs: [],
    correction: { registration: ["KEEP", "UPDATE"], decision: ["KEEP"] },
    seeds: { registration: null, decision: null, work: null },
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
const valid = () =>
  expect(
    isResponseFor("case.get", "HUMAN", {
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: current },
    }),
  ).toBe(true);
const draft = <input aria-label="Authored replacement" defaultValue="User draft value" />;

it("uses the explicit core group section for Before and keeps the authored replacement separate", () => {
  valid();
  render(
    <CorrectionInputs group="registration" current={current}>
      {draft}
    </CorrectionInputs>,
  );
  expect(screen.getByText("Core saved group caption Ω")).toBeVisible();
  expect(screen.queryByText("Other read-only facts Ψ")).not.toBeInTheDocument();
  expect(screen.getByRole("textbox", { name: "Authored replacement" })).toHaveValue(
    "User draft value",
  );
  expect(
    screen.getAllByRole("heading").every((heading) => heading.textContent?.includes("Details")),
  ).toBe(true);
});

it("removes the saved Before section synchronously when its core group disappears without clearing user input", async () => {
  valid();
  const view = render(
    <CorrectionInputs group="registration" current={current}>
      {draft}
    </CorrectionInputs>,
  );
  expect(screen.getByText("Core saved group caption Ω")).toBeVisible();
  const input = screen.getByRole("textbox", { name: "Authored replacement" });
  await userEvent.clear(input);
  await userEvent.type(input, "Edited unfinished draft");
  const withheldGroup: CurrentCase = {
    ...current,
    screen: { ...current.screen, currentGroups: [] },
  };
  view.rerender(
    <CorrectionInputs group="registration" current={withheldGroup}>
      {draft}
    </CorrectionInputs>,
  );
  expect(screen.queryByText("Core saved group caption Ω")).not.toBeInTheDocument();
  expect(screen.queryByText("Other read-only facts Ψ")).not.toBeInTheDocument();
  expect(screen.getByRole("textbox", { name: "Authored replacement" })).toHaveValue(
    "Edited unfinished draft",
  );
});
