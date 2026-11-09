import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { CurrentCase } from "../../../src/protocol/types";
import { render, screen } from "../../support/presentation-test-support";
import { CaseNotes } from "../../../src/views/case/CaseNotes";
import { ReminderList } from "../../../src/views/work/ReminderList";

const caseId = "00000000-0000-4000-8000-000000000001";
const noteId = "00000000-0000-4000-8000-000000000002";
const reminderId = "00000000-0000-4000-8000-000000000003";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
afterEach(() => vi.unstubAllGlobals());

it("renders supplied note narrative and author captions and binds remove to the Core handle", async () => {
  const current: CurrentCase = {
    screen: {
      regions: [
        {
          role: "NOTES",
          titleId: "section.notes.label",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "noteText",
                labelId: "field.noteText.label",
                prominence: "SECONDARY",
                isMoney: false,
              },
              {
                key: "recordedBy",
                labelId: "label.recordedBy",
                prominence: "PRIMARY",
                isMoney: false,
              },
            ],
            rows: [
              {
                key: noteId,
                values: [
                  { kind: "NARRATIVE", value: "Core note narrative Ω" },
                  { kind: "TEXT", value: "Core opaque author Ψ" },
                ],
              },
            ],
            sections: [
              {
                titleId: "label.notesCount",
                entries: [
                  {
                    key: "notesCount",
                    labelId: "label.notesCount",
                    prominence: "SECONDARY",
                    value: { kind: "COUNT", value: 1 },
                  },
                ],
              },
            ],
          },
        },
      ],
    },
    context: {
      kind: "CASE",
      caseId,
      revision: 7,
      workRevision: 2,
      history: "NONE",
      tasks: ["NOTE:REMOVE_NOTE"],
      editableInputs: [],
      correction: null,
      seeds: { registration: null, decision: null, work: null },
      notes: [{ noteId, canRemove: true }],
    },
    basis,
    disclosure: "CURRENT",
  };
  const remove = vi.fn();
  render(
    <CaseNotes
      current={current}
      editing={false}
      onAdd={vi.fn()}
      onRemove={remove}
      onDisclosureRefused={vi.fn()}
    />,
  );
  expect(screen.getByText("Core note narrative Ω")).toBeVisible();
  expect(screen.getByText("Core opaque author Ψ")).toBeVisible();
  await userEvent.click(screen.getByRole("button", { name: /remove note/i }));
  expect(remove).toHaveBeenCalledWith(noteId);
  expect(screen.queryByText(noteId)).not.toBeInTheDocument();
});

it("renders reminder rows from Core while actions use versioned task controls without raw reminder data", async () => {
  const control = {
    caseId,
    reminderId,
    revision: 4,
    tasks: ["REMINDER:REMINDER_COMPLETE"],
    actionSeed: null,
    dueDateSeed: null,
  };
  const document = {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "caseReference",
                labelId: "field.caseReference.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "action",
                labelId: "field.reminderAction.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
            ],
            rows: [
              {
                key: reminderId,
                values: [
                  { kind: "TEXT", value: "Core case caption Ω" },
                  { kind: "TEXT", value: "Core reminder caption Ψ" },
                ],
              },
            ],
          },
        },
      ],
    },
    context: { kind: "REMINDERS", nextCursor: null, reminders: [control] },
    basis,
    disclosure: "CURRENT",
  };
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        new Response(
          JSON.stringify({ representation: "HUMAN", outcome: { kind: "SUCCEEDED", document } }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
      ),
  );
  const act = vi.fn();
  render(<ReminderList filter={{}} onAct={act} />);
  await screen.findByText("Core reminder caption Ψ");
  expect(screen.getByRole("link", { name: "Core case caption Ω" })).toHaveAttribute(
    "href",
    expect.stringContaining(caseId),
  );
  await userEvent.click(screen.getByRole("button", { name: /complete/i }));
  expect(act).toHaveBeenCalledWith(control, "REMINDER:REMINDER_COMPLETE", expect.any(String));
});
