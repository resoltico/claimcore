import { operationAffordance } from "../../../support/affordances.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Region, ResponseFor } from "@contracts/client";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { current as bindingDocument } from "../../../support/operation-form.fixtures";
import type { CurrentCase } from "../../../../src/protocol/types";
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
            value: {
              kind: "COUNT",
              value: 7,
            },
          },
        ],
      },
    ],
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
        key: id,
        actions: [
          operationAffordance("note.remove", {
            kind: "NOTE",
            target: { kind: "ID", caseId: bindingDocument.subject.caseId },
            noteId: id,
          }),
        ],
        values: [
          {
            kind: "NARRATIVE",
            value: "Authoritative supplied note",
          },
          {
            kind: "TEXT",
            value: "Supplied author",
          },
        ],
      },
    ],
  },
};
const view: CurrentCase = {
  ...bindingDocument,
  screen: {
    regions: [supplied],
  },
  actions: [
    operationAffordance("note.add", {
      kind: "NOTE",
      target: { kind: "ID", caseId: bindingDocument.subject.caseId },
    }),
  ],
};
const withoutRowActions = (): Region => {
  const { content } = supplied;
  if (content.kind !== "TABLE") {
    throw new Error("Table fixture required");
  }
  return {
    ...supplied,
    content: { ...content, rows: content.rows.map(({ key, values }) => ({ key, values })) },
  };
};
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
  expect(screen.getByText("Supplied author")).toBeVisible();
  await userEvent.setup().click(
    screen.getByRole("button", {
      name: "Remove note",
    }),
  );
  expect(remove).toHaveBeenCalledWith(
    expect.objectContaining({
      task: "note.remove",
      binding: {
        kind: "NOTE",
        target: { kind: "ID", caseId: bindingDocument.subject.caseId },
        noteId: id,
      },
    }),
  );
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
  await user.type(
    screen.getByRole("textbox", {
      name: "Note",
    }),
    "First{enter}Second",
  );
  await user.click(
    screen.getByRole("button", {
      name: "Add note",
    }),
  );
  expect(add).toHaveBeenCalledWith("First\nSecond");
  result.rerender(
    <CaseNotes
      current={{
        ...view,
        actions: [],
        screen: { regions: [withoutRowActions()] },
      }}
      editing={false}
      onAdd={add}
      onRemove={vi.fn()}
      onDisclosureRefused={vi.fn()}
    />,
  );
  expect(screen.queryByRole("textbox")).toBeNull();
  expect(
    screen.queryByRole("button", {
      name: "Remove note",
    }),
  ).toBeNull();
});
afterEach(() => {
  vi.unstubAllGlobals();
});
it("refreshes the case when the first expanded notes page reports erasure", async () => {
  const reply = {
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: {
        screen: {
          regions: [],
        },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: view.basis,
        disclosure: "ERASED",
      },
    },
  } satisfies ResponseFor<"case.notes", "HUMAN">;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockResolvedValue(
      new Response(JSON.stringify(reply), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      }),
    ),
  );
  const refused = vi.fn();
  render(
    <CaseNotes
      current={view}
      editing={false}
      onAdd={vi.fn()}
      onRemove={vi.fn()}
      onDisclosureRefused={refused}
    />,
  );
  await userEvent.setup().click(
    screen.getByRole("button", {
      name: "Show all notes",
    }),
  );
  await waitFor(() => {
    expect(refused).toHaveBeenCalled();
  });
  expect(screen.queryByText("Authoritative supplied note")).not.toBeInTheDocument();
});
