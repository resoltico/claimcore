import { act, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { RecoveryItem } from "@contracts/client";
import { render, screen } from "./presentation-test-support";
import { wireReply } from "./contract-corpus.fixtures";
import { definition } from "./service-ui.fixtures";
import { service } from "../src/api/service";
import { RecoveryRequests } from "../src/views/operation/RecoveryRequests";
import { hook, preview } from "./operation-hook.fixtures";
afterEach(() => vi.restoreAllMocks());
const sample = () => {
  const reply = wireReply("operation.execute");
  if (reply.outcome.tag !== "ACCEPTED" || reply.outcome.data.scope.kind !== "CASE") {
    throw new Error("Case acceptance sample missing");
  }
  const { scope } = reply.outcome.data;
  const item: RecoveryItem = {
    recoveryId: "20000000-0000-4000-8000-000000000001",
    operationId: reply.operationId,
    scope: "CASE",
    act: scope.act,
    caseReference: scope.caseReference,
    expectedRevision: scope.revision - 1,
    targetActorId: null,
    createdAt: "2026-10-24T00:00:00.000000Z",
    expiresAt: "2026-10-31T00:00:00.000000Z",
    offers: ["RETRY", "FORGET"],
  };
  return { item, reply };
};
it("lists metadata through a short continuation, selects without execution and acknowledges an explicit opaque retry", async () => {
  const { item, reply } = sample();
  const list = vi
    .spyOn(service, "pending")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: { outcome: { tag: "SUCCEEDED", data: { items: [], nextCursor: "next" } } },
    })
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { outcome: { tag: "SUCCEEDED", data: { items: [item], nextCursor: null } } },
    });
  const retry = vi.spyOn(service, "retry").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { ...reply, recoveryId: item.recoveryId },
  });
  const forget = vi.spyOn(service, "forget").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { tag: "SUCCEEDED", data: null } },
  });
  const user = userEvent.setup();
  render(<RecoveryRequests token="token" definition={definition} onLockChange={vi.fn()} />);
  expect(list).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "List submitted requests" }));
  await user.click(await screen.findByRole("button", { name: "More submitted requests" }));
  await user.click(await screen.findByRole("button", { name: "Select submitted request" }));
  expect(list.mock.calls[1]?.[0]).toBe("next");
  expect(retry).not.toHaveBeenCalled();
  expect(screen.queryByRole("textbox")).toBeNull();
  expect(screen.getByText(/A retry may accept this submitted request now/u)).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Retry the same operation" }));
  const acknowledged = await screen.findByRole("button", {
    name: "Acknowledge result and return to requests",
  });
  expect(retry).toHaveBeenCalledExactlyOnceWith(item.recoveryId, item.operationId, "token");
  expect(forget).not.toHaveBeenCalled();
  await user.click(acknowledged);
  expect(forget).toHaveBeenCalledExactlyOnceWith(item.recoveryId, "token");
});
it("forgets only after a warning that removal neither cancels nor establishes nonacceptance", async () => {
  const { item } = sample();
  vi.spyOn(service, "pending").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { tag: "SUCCEEDED", data: { items: [item], nextCursor: null } } },
  });
  const forget = vi.spyOn(service, "forget").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { tag: "SUCCEEDED", data: null } },
  });
  const user = userEvent.setup();
  render(<RecoveryRequests token="token" definition={definition} onLockChange={vi.fn()} />);
  await user.click(screen.getByRole("button", { name: "List submitted requests" }));
  await user.click(await screen.findByRole("button", { name: "Forget submitted request" }));
  expect(forget).not.toHaveBeenCalled();
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("does not cancel the operation or prove it was not accepted");
  await user.click(within(dialog).getByRole("button", { name: "Forget submitted request" }));
  expect(forget).toHaveBeenCalledOnce();
});
it("known result cleanup is explicit and a failed acknowledgement cannot alter acceptance", async () => {
  const { reply } = sample();
  vi.spyOn(service, "preview").mockResolvedValue(preview());
  vi.spyOn(service, "execute").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { ...reply, recoveryId: "20000000-0000-4000-8000-000000000001" },
  });
  const forget = vi.spyOn(service, "forget").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const view = hook();
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
  await act(async () => {
    await view.result.current.execute();
  });
  await waitFor(() => {
    expect(view.result.current.phase).toBe("ACCEPTED");
  });
  expect(forget).not.toHaveBeenCalled();
  act(() => {
    view.result.current.acknowledge();
  });
  expect(forget).toHaveBeenCalledOnce();
  expect(view.result.current.phase).toBe("ACCEPTED");
  expect(view.result.current.priorUncertain).toBe(false);
});
it("a mismatched recovered receipt keeps opaque uncertainty and cannot trigger acknowledgement cleanup", async () => {
  const { item, reply } = sample();
  if (reply.outcome.tag !== "ACCEPTED" || reply.outcome.data.scope.kind !== "CASE") {
    throw new Error("Case acceptance sample missing");
  }
  vi.spyOn(service, "retry").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...reply,
      recoveryId: item.recoveryId,
      outcome: {
        tag: "ACCEPTED",
        data: {
          ...reply.outcome.data,
          scope: { ...reply.outcome.data.scope, revision: reply.outcome.data.scope.revision + 1 },
        },
      },
    },
  });
  const forget = vi.spyOn(service, "forget");
  const view = hook();
  act(() => {
    view.result.current.recover(item);
  });
  expect(view.result.current.prepared).toBeNull();
  await act(async () => {
    await view.result.current.execute();
  });
  expect(view.result.current.phase).toBe("UNCERTAIN");
  expect(view.result.current.receipt).toBeNull();
  expect(view.result.current.recovery).toEqual(item);
  act(() => {
    view.result.current.acknowledge();
  });
  expect(forget).not.toHaveBeenCalled();
});
it("a recovered item without a current retry offer cannot start a ledger effect", async () => {
  const { item } = sample();
  const retry = vi.spyOn(service, "retry");
  const view = hook();
  act(() => {
    view.result.current.recover({ ...item, offers: ["FORGET"] });
  });
  await act(async () => {
    await view.result.current.execute();
  });
  expect(view.result.current.phase).toBe("UNCERTAIN");
  expect(retry).not.toHaveBeenCalled();
});
