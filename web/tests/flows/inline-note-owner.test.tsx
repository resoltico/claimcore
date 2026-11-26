import type { OwnerScope } from "../../src/protocol/ownerScope";
import { useLayoutEffect } from "react";
import { act } from "@testing-library/react";
import { expect, it } from "vitest";
import { render, screen, testWorkspaceIdentity } from "../support/presentation-test-support";
import {
  InlineNoteDraftProvider,
  useInlineNoteDraft,
  type NoteIntent,
} from "../../src/features/case/InlineNoteDraft";
const id = "20000000-0000-4000-8000-000000000001";
let notes: ReturnType<typeof useInlineNoteDraft>;
const Probe = ({ caseId = id }: { caseId?: string }) => {
  const state = useInlineNoteDraft();
  const { observe } = state;
  useLayoutEffect(() => {
    notes = state;
  }, [state]);
  useLayoutEffect(() => {
    observe(caseId, "FIELD-REF");
  }, [observe, caseId]);
  return <output>{state.draft?.caseId === caseId ? state.draft.text : "masked"}</output>;
};
it("retains ephemeral text across disclosure unmounts and unresolved owner, but never another owner or case", () => {
  const body = (owner: typeof testWorkspaceIdentity | null, visible = true, caseId = id) => (
    <InlineNoteDraftProvider owner={owner}>
      {visible ? <Probe caseId={caseId} /> : null}
    </InlineNoteDraftProvider>
  );
  const view = render(body(testWorkspaceIdentity));
  act(() => {
    notes.edit(id, "Unsent exact\nثانٍ");
  });
  view.rerender(body(null, false));
  const departure = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(departure);
  expect(departure.defaultPrevented).toBe(true);
  view.rerender(body(null));
  expect(screen.getByRole("status")).toHaveTextContent("masked");
  view.rerender(body(testWorkspaceIdentity));
  expect(screen.getByRole("status").textContent).toBe("Unsent exact\nثانٍ");
  view.rerender(body(testWorkspaceIdentity, true, "another-case"));
  expect(screen.getByRole("status").textContent).toBe("");
  view.rerender(body(testWorkspaceIdentity));
  act(() => {
    notes.edit(id, "owner private");
  });
  view.rerender(body({ ...testWorkspaceIdentity, actorId: "another-actor" }));
  expect(screen.getByRole("status").textContent).toBe("");
});
it("clears only its own unchanged accepted generation, including identity-only recovery", () => {
  render(<Probe />);
  let old!: NoteIntent;
  act(() => {
    notes.edit(id, "first");
  });
  act(() => {
    old = notes.capture(id)!;
  });
  act(() => {
    notes.bind(old, "old-operation");
  });
  act(() => {
    notes.edit(id, "new exact text");
  });
  act(() => {
    notes.accepted(old);
    notes.reconciled("old-operation", id);
  });
  expect(screen.getByRole("status")).toHaveTextContent("new exact text");
  let own!: NoteIntent;
  act(() => {
    own = notes.capture(id)!;
  });
  act(() => {
    notes.bind(own, "own-operation");
  });
  act(() => {
    notes.reconciled("foreign-operation", id);
    notes.reconciled("own-operation", "foreign-case");
  });
  expect(screen.getByRole("status")).toHaveTextContent("new exact text");
  act(() => {
    notes.reconciled("own-operation", id);
  });
  expect(screen.getByRole("status").textContent).toBe("");
});

it.each([
  { ...testWorkspaceIdentity, actorId: "another-actor" },
  { ...testWorkspaceIdentity, installationId: "another-installation" },
  { ...testWorkspaceIdentity, sessionEpoch: 1 },
  { ...testWorkspaceIdentity, credential: "replacement-credential" },
])("never revives inline text after an actual owner component is replaced: %j", (replacement) => {
  const body = (owner: OwnerScope & { credential: string }) => (
    <InlineNoteDraftProvider owner={owner}>
      <Probe />
    </InlineNoteDraftProvider>
  );
  const view = render(body(testWorkspaceIdentity));
  act(() => {
    notes.edit(id, "Previous owner confidential draft");
  });
  view.rerender(body(replacement));
  expect(screen.getByRole("status").textContent).not.toContain("Previous owner");
  view.rerender(body(testWorkspaceIdentity));
  expect(screen.getByRole("status").textContent).toBe("");
});
