import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { RecoveryPage } from "../src/views/recovery/RecoveryPage";
import {
  RecoveryConfirmDialog,
  RecoveryDetailsDialog,
  RecoveryImportDialog,
  RecoveryList,
  type Listing,
} from "../src/views/recovery/RecoveryPanels";
import type { RecoveryActions } from "../src/views/recovery/RecoveryState";
import { fields, operationId, preparation } from "./v2-ui.fixtures";

const actions = (): RecoveryActions => ({
  inspect: vi.fn(),
  loadAttempts: vi.fn(),
  choose: vi.fn(),
  act: vi.fn(),
  exportItem: vi.fn(),
  preview: vi.fn(),
  retain: vi.fn(),
});

const listing = (cursor: string | null = null): Listing => ({
  items: [{ tag: "RETAINED" as const, summary: preparation.summary }],
  cursor,
  message: "Synthetic recovery error",
  loading: false,
  page: {
    view: "PENDING" as const,
    items: [],
    nextCursor: cursor,
    pendingPreparationCount: 1,
    pendingCanonicalRequestBytes: 99,
    maximumPendingPreparations: 1024,
    maximumPendingCanonicalRequestBytes: 64 * 1024 * 1024,
    nearCapacity: false,
  },
  view: "PENDING" as const,
  setView: vi.fn(),
  load: vi.fn(() => Promise.resolve()),
});

it("renders recovery list state, invokes inspection, and pages through a cursor", async () => {
  const user = userEvent.setup();
  const action = actions();
  const page = listing("next");
  render(<RecoveryList listing={page} busy={null} actions={action} />);
  expect(screen.getByRole("alert")).toHaveTextContent("Synthetic recovery error");
  await user.click(screen.getByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Load more recovery" }));
  expect(action.inspect).toHaveBeenCalledWith({ tag: "RETAINED", summary: preparation.summary });
  expect(page.load).toHaveBeenCalledWith("next");
});

it("renders observed recovery details and leaves only permitted server actions enabled", async () => {
  const user = userEvent.setup();
  const action = actions();
  const close = vi.fn();
  const acceptedSummary = { ...preparation.summary, availableActions: ["EXPORT"] as const };
  render(
    <RecoveryDetailsDialog
      selected={{
        tag: "RETAINED",
        value: {
          preparation,
          observation: {
            tag: "FOUND",
            value: {
              operationId,
              snapshot: { fields, revision: "1" },
              recordedAt: "2026-09-09T00:00:00.0000000+00:00",
              recordedBy: "synthetic",
              replayed: false,
              command: "CLOSE",
            },
          },
        },
      }}
      summary={acceptedSummary}
      onClose={close}
      actions={action}
    />,
  );
  expect(screen.getAllByText(operationId)).toHaveLength(2);
  expect(screen.queryByRole("button", { name: "Resolve exact preparation" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Dismiss preparation" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "Export recovery envelope" }));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  expect(action.choose).not.toHaveBeenCalled();
  expect(action.exportItem).toHaveBeenCalledWith(acceptedSummary);
  expect(close).toHaveBeenCalledOnce();
});

it("keeps recovery confirm dialog accessible before dispatch", async () => {
  const user = userEvent.setup();
  const action = actions();
  const closeConfirm = vi.fn();
  render(
    <RecoveryConfirmDialog
      confirm={{ action: "RESOLVE", item: preparation.summary }}
      busy={null}
      onClose={closeConfirm}
      actions={action}
    />,
  );
  await user.click(screen.getByRole("button", { name: "Confirm resolve" }));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  expect(action.act).toHaveBeenCalledOnce();
  expect(closeConfirm).toHaveBeenCalledOnce();
});

it("keeps recovery import dialog accessible before retention", async () => {
  const user = userEvent.setup();
  const action = actions();
  const closeImport = vi.fn();
  const file = new File(["{}"], "recovery.json", { type: "application/json" });
  render(
    <RecoveryImportDialog
      importing={{
        file,
        kind: "ENVELOPE",
        preview: {
          artifactKind: "ENVELOPE",
          sourceSha256: "c".repeat(64),
          decodedEffect: {
            operationId,
            caseReference: "CASE-1",
            command: "CLOSE",
            expectedRevision: "1",
            authoredValues: [],
            canonicalCommandFormat: 2,
            requestSha256: "b".repeat(64),
          },
          existingPreparation: null,
        },
      }}
      busy={null}
      onClose={closeImport}
      actions={action}
    />,
  );
  await user.click(screen.getByRole("button", { name: "Retain for Recovery" }));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  expect(action.retain).toHaveBeenCalledOnce();
  expect(closeImport).toHaveBeenCalledOnce();
});

it("renders recovery import controls and lets the page reload without mutation", async () => {
  const user = userEvent.setup();
  const action = actions();
  const page = listing();
  render(
    <RecoveryPage
      listing={page}
      selected={null}
      summary={null}
      confirm={null}
      importing={null}
      message={null}
      busy={null}
      actions={action}
      closeDetails={vi.fn()}
      closeConfirm={vi.fn()}
      closeImport={vi.fn()}
    />,
  );
  expect(screen.getByRole("button", { name: "Import recovery envelope" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Reload" }));
  expect(page.load).toHaveBeenCalledWith(null);
});

it("renders terminal revocation authority and exposes no mutation action", () => {
  const action = actions();
  const view = listing();
  view.view = "TERMINAL";
  if (view.page === null) throw new Error("Expected a recovery page.");
  view.page = {
    ...view.page,
    view: "TERMINAL",
    nearCapacity: true,
    pendingPreparationCount: 1020,
  };
  const revoked = {
    tag: "REVOKED" as const,
    revocation: {
      operationId,
      revokedAt: "2026-09-10T00:00:00.0000000+00:00",
      reason: "Dismissed before execution.",
    },
  };
  view.items = [revoked];
  render(
    <>
      <RecoveryPage
        listing={view}
        selected={null}
        summary={null}
        confirm={null}
        importing={null}
        message={null}
        busy={null}
        actions={action}
        closeDetails={vi.fn()}
        closeConfirm={vi.fn()}
        closeImport={vi.fn()}
      />
      <RecoveryDetailsDialog
        selected={{ tag: "REVOKED", revocation: revoked.revocation }}
        summary={null}
        onClose={vi.fn()}
        actions={action}
      />
    </>,
  );
  expect(screen.getByText(/Pending recovery capacity/u)).toHaveTextContent("1020 / 1024");
  expect(screen.getByText(/cannot be resurrected/u)).toBeVisible();
  expect(screen.queryByRole("button", { name: "Resolve exact preparation" })).toBeNull();
});

it("switches recovery views explicitly and leaves unavailable capacity unrendered", async () => {
  const user = userEvent.setup();
  const action = actions();
  const view = listing();
  view.page = null;
  render(
    <RecoveryPage
      listing={view}
      selected={null}
      summary={null}
      confirm={null}
      importing={null}
      message={null}
      busy={null}
      actions={action}
      closeDetails={vi.fn()}
      closeConfirm={vi.fn()}
      closeImport={vi.fn()}
    />,
  );
  expect(screen.queryByText(/Pending recovery capacity/u)).toBeNull();
  const selector = screen.getByLabelText("Recovery view");
  await user.selectOptions(selector, "TERMINAL");
  await user.selectOptions(selector, "PENDING");
  expect(view.setView).toHaveBeenNthCalledWith(1, "TERMINAL");
  expect(view.setView).toHaveBeenNthCalledWith(2, "PENDING");
});

it("requests the next server attempt page only from an inspected retained item", async () => {
  const user = userEvent.setup();
  const action = actions();
  const attempts = {
    items: [
      {
        attemptId: operationId,
        startedAt: "2026-09-10T00:00:00.0000000+00:00",
        settlement: null,
        settledAt: null,
      },
    ],
    nextCursor: "next-attempt",
    legacyUncertainty: true,
  } as const;
  render(
    <RecoveryDetailsDialog
      selected={{
        tag: "RETAINED",
        value: {
          preparation: { ...preparation, attempts },
          observation: { tag: "NOT_FOUND", identity: operationId },
        },
      }}
      summary={preparation.summary}
      onClose={vi.fn()}
      actions={action}
    />,
  );
  expect(screen.getByText(/legacy uncertainty remains/u)).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Load more attempts" }));
  expect(action.loadAttempts).toHaveBeenCalledWith(operationId, "next-attempt");
});
