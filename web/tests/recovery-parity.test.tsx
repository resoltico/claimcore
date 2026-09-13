import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { PreparationDetails } from "../src/api/v2";
import { RecoveryView } from "../src/views/RecoveryView";
import {
  onceWhilePending,
  recoveryActions,
  type RecoveryUi,
} from "../src/views/recovery/RecoveryState";
import { generatedWebValue } from "./contract-corpus.fixtures";
import { fields, operationId, preparation, response } from "./v2-ui.fixtures";

const list = (items: unknown[] = [preparation.summary]) =>
  response("recovery.list", "SUCCEEDED", { items, nextCursor: null });

const inspection = (details: PreparationDetails = preparation) =>
  response("recovery.inspect", "SUCCEEDED", {
    tag: "FOUND",
    value: { preparation: details, observation: { tag: "NOT_FOUND", identity: operationId } },
  });

const recoveryUi = (changes: Partial<RecoveryUi>): RecoveryUi => ({
  selected: null,
  setSelected: vi.fn(),
  selectedSummary: null,
  setSelectedSummary: vi.fn(),
  confirm: null,
  setConfirm: vi.fn(),
  importing: null,
  setImporting: vi.fn(),
  message: null,
  setMessage: vi.fn(),
  busy: null,
  setBusy: vi.fn(),
  ...changes,
});

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("requires fresh inspection before retrying a submission-started preparation", async () => {
  const user = userEvent.setup();
  const started: PreparationDetails = {
    ...preparation,
    summary: {
      ...preparation.summary,
      state: "SUBMISSION_STARTED",
      availableActions: ["RESOLVE", "EXPORT"],
    },
  };
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list([started.summary]));
  fetch.mockResolvedValueOnce(inspection(started));
  render(<RecoveryView token="token" />);
  await screen.findByRole("button", { name: "Inspect" });
  expect(screen.queryByRole("button", { name: "Resolve exact preparation" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "Inspect" }));
  expect(await screen.findByRole("button", { name: "Resolve exact preparation" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "Dismiss preparation" })).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("uses inspected accepted evidence to remove stale list mutation actions", async () => {
  const user = userEvent.setup();
  const acceptedSummary = { ...preparation.summary, availableActions: ["EXPORT"] as const };
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(
    response("recovery.inspect", "SUCCEEDED", {
      tag: "FOUND",
      value: {
        preparation: { ...preparation, summary: acceptedSummary },
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
    }),
  );
  render(<RecoveryView token="token" />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  expect(await screen.findByRole("dialog", { name: "Recovery details" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "Resolve exact preparation" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Dismiss preparation" })).toBeNull();
  expect(screen.getByRole("button", { name: "Export recovery envelope" })).toBeVisible();
});

it("submits a retained operation ID and digest only once under duplicate confirmation", async () => {
  let fail: ((reason: Error) => void) | undefined;
  const pending = new Promise<Response>((_resolve, reject) => {
    fail = reject;
  });
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(pending);
  const ui = recoveryUi({ confirm: { action: "RESOLVE", item: preparation.summary } });
  const actions = recoveryActions("token", { load: vi.fn(() => Promise.resolve()) }, ui);
  const pendingAction = { current: false };
  void onceWhilePending(pendingAction, actions.act);
  void onceWhilePending(pendingAction, actions.act);
  expect(fetch).toHaveBeenCalledOnce();
  expect(fetch.mock.calls[0]?.[1]?.body).toBe(
    JSON.stringify({ operationId, requestSha256: preparation.summary.requestSha256 }),
  );
  fail?.(new Error("Synthetic delivery loss"));
  await waitFor(() => expect(ui.setMessage).toHaveBeenCalledOnce());
});

it("coalesces duplicate recovery retention for the same previewed bytes", async () => {
  const preview = generatedWebValue("recovery.importEnvelopePreview");
  if (preview.outcome.tag !== "SUCCEEDED") throw new Error("Expected a valid synthetic preview.");
  const file = new File(["{}"], "recovery.json");
  let fail: ((reason: Error) => void) | undefined;
  const pending = new Promise<Response>((_resolve, reject) => {
    fail = reject;
  });
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockReturnValueOnce(pending);
  const ui = recoveryUi({ importing: { file, kind: "ENVELOPE", preview: preview.outcome.data } });
  const actions = recoveryActions("token", { load: vi.fn(() => Promise.resolve()) }, ui);
  const pendingAction = { current: false };
  void onceWhilePending(pendingAction, actions.retain);
  void onceWhilePending(pendingAction, actions.retain);
  await waitFor(() => expect(fetch).toHaveBeenCalledOnce());
  expect(fetch.mock.calls[0]?.[1]?.headers).toMatchObject({
    "X-ClaimCore-Source-Sha256": preview.outcome.data.sourceSha256,
  });
  fail?.(new Error("Synthetic delivery loss"));
  await waitFor(() => expect(ui.setMessage).toHaveBeenCalledOnce());
});

it("keeps an unknown recovery result explicit and directs inspection", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockRejectedValueOnce(new Error("Synthetic delivery loss"));
  fetch.mockResolvedValueOnce(list([]));
  render(<RecoveryView token="token" />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Resolve exact preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
  expect(
    await screen.findByText(/Inspect Recovery before retrying this exact preparation/u),
  ).toBeVisible();
  expect(screen.queryByText(/Accepted exact operation/u)).toBeNull();
});
