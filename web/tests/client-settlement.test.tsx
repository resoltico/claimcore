import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { editor, preparedReply } from "./presentation-state.fixtures";
import { operationId, preparation, response } from "./v3-ui.fixtures";
import { list, inspection, receipt } from "./v3-recovery.fixtures";
import { RecoveryView } from "../src/views/RecoveryView";

const unsettled = (endpoint: string) =>
  response(endpoint, "COMPLETED", {
    preparation: preparation.summary,
    attemptId: operationId,
    execution: { tag: "ACCEPTED", receipt },
    settlement: "UNCONFIRMED",
  });
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("shows command acceptance together with required recovery for unconfirmed settlement", async () => {
  const user = userEvent.setup();
  const recover = vi.fn();
  vi.mocked(globalThis.fetch)
    .mockImplementationOnce(() => Promise.resolve(preparedReply()))
    .mockResolvedValueOnce(unsettled("command.execute"));
  render(editor({ onRecovery: recover }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  expect(await screen.findByRole("heading", { name: "Accepted operation" })).toBeVisible();
  expect(screen.getByRole("alert")).toHaveTextContent("Inspect Recovery");
  expect(recover).not.toHaveBeenCalled();
});

it("keeps accepted recovery evidence distinct from unconfirmed settlement and reports the exact subject", async () => {
  const user = userEvent.setup();
  const recover = vi.fn();
  const lock = vi.fn();
  vi.mocked(globalThis.fetch)
    .mockResolvedValueOnce(list())
    .mockResolvedValueOnce(inspection())
    .mockResolvedValueOnce(unsettled("recovery.resolve"))
    .mockResolvedValueOnce(list([]));
  render(<RecoveryView token="token" onRecovery={recover} onMutationLockChange={lock} />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(await screen.findByRole("button", { name: "Resolve exact preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
  expect(await screen.findByRole("status")).toHaveTextContent("Accepted exact operation");
  expect(screen.getByRole("status")).toHaveTextContent("Inspect Recovery");
  expect(recover).toHaveBeenCalledWith({
    operationId,
    requestSha256: preparation.summary.requestSha256,
    message: {
      kind: "recovery",
      cause: { kind: "accepted", operationId },
      direction: "inspectBeforeAction",
    },
  });
  expect(lock.mock.calls).toEqual([[true], [false]]);
});
