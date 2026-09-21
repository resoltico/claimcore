import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase, Rejection } from "../src/api/v2";
import { isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, operationId, preparation, response } from "./v2-ui.fixtures";

const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};

const review = {
  before: current.case,
  proposed: { fields, revision: "2" },
  changes: [],
  context: definition.runtime,
  advisory: true,
};

const receipt = {
  operationId,
  snapshot: { fields, revision: "2" },
  recordedAt: "2026-09-09T00:00:00.0000000+00:00",
  recordedBy: "synthetic",
  replayed: true,
  command: "CLOSE",
};

const renderEditor = (committed = vi.fn(), locked = vi.fn()) =>
  render(
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialCommand="CLOSE"
      onClose={vi.fn()}
      onCommitted={committed}
      onMutationLockChange={locked}
    />,
  );

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("offers one explicit exact-ID Prepare retry after lost response", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockRejectedValueOnce(new Error("Synthetic delivery loss"));
  fetch.mockResolvedValueOnce(
    response("command.prepare", "PREPARED", { details: preparation, review }),
  );
  renderEditor();
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(await screen.findByRole("button", { name: "Retry exact prepare" })).toBeVisible();
  expect(fetch).toHaveBeenCalledOnce();
  await user.click(screen.getByRole("button", { name: "Retry exact prepare" }));
  expect(await screen.findByRole("dialog", { name: "Review prepared operation" })).toBeVisible();
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls[1]?.[1]?.body).toBe(fetch.mock.calls[0]?.[1]?.body);
});

it("renders an exact accepted Prepare replay as a definite receipt", async () => {
  const user = userEvent.setup();
  const committed = vi.fn();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("command.prepare", "OBSERVED_ACCEPTED", { receipt }),
  );
  renderEditor(committed);
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(await screen.findByRole("heading", { name: /^Accepted operation$/u })).toBeVisible();
  expect(screen.queryByRole("dialog", { name: "Review prepared operation" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
});

it("rejects technical preparation details on an accepted Prepare replay", async () => {
  const accepted = {
    endpoint: "command.prepare",
    outcome: { tag: "OBSERVED_ACCEPTED", data: { receipt } },
  };
  expect(await isWebV2Response("command.prepare", accepted)).toBe(true);
  expect(
    await isWebV2Response("command.prepare", {
      ...accepted,
      outcome: { ...accepted.outcome, data: { receipt, details: preparation } },
    }),
  ).toBe(false);
});

it("keeps a non-reviewable retained Prepare exact and directs Recovery", async () => {
  const user = userEvent.setup();
  const locked = vi.fn();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("command.prepare", "RETAINED_FOR_RECOVERY", {
      details: preparation,
      rejection: {
        code: "VERSION_CONFLICT",
        message: "The case changed after preparation.",
        diagnostic: { id: "CASE_REVISION_CONFLICT", parameters: {} },
        field: null,
        actualRevision: "2",
        recommendedAction: "READ_CURRENT",
      } satisfies Rejection,
    }),
  );
  renderEditor(vi.fn(), locked);
  await user.click(screen.getByRole("button", { name: "Prepare exact request" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Inspect Recovery");
  expect(screen.queryByRole("dialog", { name: "Review prepared operation" })).toBeNull();
  expect(screen.getByRole("button", { name: "Back without preparing" })).toBeDisabled();
  expect(locked).toHaveBeenCalledWith(true);
  expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledOnce();
});
