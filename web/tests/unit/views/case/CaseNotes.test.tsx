import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Region } from "@contracts/client";
import { render, screen } from "../../../support/presentation-test-support";
import { currentCaseInput, registration } from "../../../support/inputs";
import { caseAdvice } from "../../../support/case-advice.fixtures";
import { CaseNotes } from "../../../../src/views/case/CaseNotes";

const id = "10000000-0000-7000-8000-000000000091";
const supplied: Region = {
  role: "NOTES",
  titleId: "section.notes.label",
  content: {
    kind: "TABLE",
    sections: [
      {
        titleId: "label.notesCount",
        entries: [
          {
            key: "notesCount",
            labelId: "label.notesCount",
            prominence: "SECONDARY",
            value: { kind: "COUNT", value: 7 },
          },
        ],
      },
    ],
    columns: [
      { key: "noteText", labelId: "field.noteText.label", prominence: "SECONDARY", isMoney: false },
      { key: "recordedBy", labelId: "label.recordedBy", prominence: "PRIMARY", isMoney: false },
    ],
    rows: [
      {
        key: id,
        values: [
          { kind: "NARRATIVE", value: "Authoritative supplied note" },
          { kind: "TEXT", value: "Supplied author" },
        ],
      },
    ],
  },
};
const current = currentCaseInput({
  caseReference: "NOTES",
  revision: 1,
  advice: { ...caseAdvice([]), history: "NONE" },
  state: {
    kind: "ACTIVE",
    fields: {
      caseReference: "NOTES",
      status: "OPENED",
      registration: registration(),
      decision: null,
      payments: [],
      progress: "UNDECIDED",
    },
  },
  notes: {
    items: [
      {
        noteId: id,
        text: "Typed text must not be rendered",
        recordedAt: "2026-09-01T00:00:00.000000Z",
        recordedBy: {
          actorId: id,
          displayName: { kind: "ERASED" },
          principalKind: "HUMAN",
          availability: "AVAILABLE",
        },
        canRemove: true,
      },
    ],
    count: 1,
    canAdd: true,
  },
});
const view = { ...current, view: { regions: [supplied] } };
it("renders Core note text without history access and binds removal to the supplied row key", async () => {
  const remove = vi.fn();
  render(
    <CaseNotes
      current={view}
      editing={false}
      onAdd={vi.fn()}
      onRemove={remove}
      onDisclosureRefused={vi.fn()}
    />,
  );
  expect(screen.getByText("Authoritative supplied note")).toBeVisible();
  expect(screen.getByText("Shown: 1 of 7")).toBeVisible();
  expect(screen.queryByText("Typed text must not be rendered")).toBeNull();
  await userEvent.setup().click(screen.getByRole("button", { name: "Remove" }));
  expect(remove).toHaveBeenCalledWith(id);
});
it("keeps the composed text exact for shared review and obeys current permission flags", async () => {
  const add = vi.fn();
  const user = userEvent.setup();
  const result = render(
    <CaseNotes
      current={view}
      editing={false}
      onAdd={add}
      onRemove={vi.fn()}
      onDisclosureRefused={vi.fn()}
    />,
  );
  await user.type(screen.getByRole("textbox", { name: "Note" }), "First{enter}Second");
  await user.click(screen.getByRole("button", { name: "Add note" }));
  expect(add).toHaveBeenCalledWith("First\nSecond");
  result.rerender(
    <CaseNotes
      current={{
        ...view,
        notes: {
          ...view.notes,
          canAdd: false,
          items: [{ ...view.notes.items[0]!, canRemove: false }],
        },
      }}
      editing={false}
      onAdd={add}
      onRemove={vi.fn()}
      onDisclosureRefused={vi.fn()}
    />,
  );
  expect(screen.queryByRole("textbox")).toBeNull();
  expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
});
