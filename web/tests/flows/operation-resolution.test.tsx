import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { current } from "../support/operation-form.fixtures";
import { act, waitFor, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { service } from "../../src/api/service";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { preview } from "../support/operation-hook.fixtures";
import {
  recoveryOptions,
  recoveryRecord,
  recoveryScope,
  testJournal,
} from "../support/recovery.fixtures";
import type { RecoveryJournal } from "../../src/protocol/recoveryJournal";
afterEach(() => {
  vi.restoreAllMocks();
});
it("automatic recovery checks each saved identity without resubmitting", async () => {
  const first = recoveryRecord();
  const second = {
    ...recoveryRecord("10000000-0000-4000-8000-000000000003"),
    operationId: "10000000-0000-7000-8000-000000000004",
  };
  await testJournal.put(first);
  await testJournal.put(second);
  const lookup = vi.spyOn(service, "operationGet").mockImplementation((operationId) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: { operationId, outcome: { kind: "NOT_ACCEPTED", data: null } },
    }),
  );
  const execute = vi.spyOn(service, "execute");
  render(
    <RecoveryProvider>
      <UnresolvedNotice />
    </RecoveryProvider>,
  );
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  expect(lookup).toHaveBeenCalledTimes(2);
  expect(execute).not.toHaveBeenCalled();
});
const closeOperation = (journal: RecoveryJournal = testJournal) =>
  renderHook(
    ({ credential }) =>
      useOperation({
        ...recoveryOptions,
        journal,
        subject: "CASE",
        kind: "CLOSE",
        current,
        credential,
        onLockChange: vi.fn(),
      }),
    { initialProps: { credential: "token" } },
  );
const reviewClose = async (view: ReturnType<typeof closeOperation>) => {
  await act(async () => {
    await view.result.current.preview();
  });
  act(() => {
    view.result.current.confirm(true);
  });
};
it("refreshes an old provisional ID at first dispatch while preserving the reviewed change", async () => {
  const now = Date.now();
  const clock = vi.spyOn(Date, "now").mockReturnValue(now - 10 * 60_000);
  vi.spyOn(service, "preview").mockImplementation((prepared) => Promise.resolve(preview(prepared)));
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const view = closeOperation();
  await reviewClose(view);
  const before = view.result.current.prepared!.input;
  clock.mockReturnValue(now);
  act(() => {
    void view.result.current.execute();
  });
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(1);
  });
  const [sent] = execute.mock.calls[0]!;
  expect(sent.input.operationId).not.toBe(before.operationId);
  expect(Number.parseInt(sent.input.operationId.replaceAll("-", "").slice(0, 12), 16)).toBe(now);
  expect({ ...sent.input, operationId: before.operationId }).toEqual(before);
  expect((await testJournal.list(recoveryScope))[0]?.operationId).toBe(sent.input.operationId);
});

it("an exact retry after expiry keeps the dispatched ID and bytes", async () => {
  const now = Date.now();
  const clock = vi.spyOn(Date, "now").mockReturnValue(now);
  vi.spyOn(service, "preview").mockImplementation((prepared) => Promise.resolve(preview(prepared)));
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const view = closeOperation();
  await reviewClose(view);
  act(() => {
    void view.result.current.execute();
  });
  await waitFor(() => {
    expect(view.result.current.phase).toBe("UNRESOLVED");
  });
  const [first] = execute.mock.calls[0]!;
  clock.mockReturnValue(now + 11 * 60_000);
  act(() => {
    void view.result.current.execute();
  });
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(2);
  });
  expect(execute.mock.calls[1]![0].input.operationId).toBe(first.input.operationId);
  expect(execute.mock.calls[1]![0].bytes).toBe(first.bytes);
  expect(await testJournal.list(recoveryScope)).toHaveLength(1);
});

it("a persisted attempt prevents provisional re-minting in a replacement editor", async () => {
  const now = Date.now();
  const clock = vi.spyOn(Date, "now").mockReturnValue(now - 10 * 60_000);
  vi.spyOn(service, "preview").mockImplementation((prepared) => Promise.resolve(preview(prepared)));
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  const view = closeOperation();
  await reviewClose(view);
  const before = view.result.current.prepared!;
  const existing = { ...recoveryRecord(), operationId: before.input.operationId };
  await testJournal.put(existing);
  clock.mockReturnValue(now);
  act(() => {
    void view.result.current.execute();
  });
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(1);
  });
  expect(execute.mock.calls[0]![0].input.operationId).toBe(before.input.operationId);
  expect(execute.mock.calls[0]![0].bytes).toBe(before.bytes);
  expect(await testJournal.list(recoveryScope)).toContainEqual(existing);
});

it("the journal commits before a pending submission can be handed off", async () => {
  vi.spyOn(service, "preview").mockImplementation((prepared) => Promise.resolve(preview(prepared)));
  const execute = vi.spyOn(service, "execute").mockImplementation(
    () =>
      new Promise(() => {
        /* Deliberately hold the transport reply. */
      }),
  );
  let commit!: () => Promise<void>;
  const journal: RecoveryJournal = {
    ...testJournal,
    put: (record) =>
      new Promise<void>((resolve) => {
        commit = () => testJournal.put(record).then(resolve);
      }),
  };
  const view = closeOperation(journal);
  await reviewClose(view);
  act(() => {
    void view.result.current.execute();
  });
  await waitFor(() => {
    expect(view.result.current.phase).toBe("PREPARING");
  });
  expect(execute).not.toHaveBeenCalled();
  await act(async () => {
    await commit();
  });
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(1);
  });
  const records = await testJournal.list(recoveryScope);
  expect(records).toHaveLength(1);
  expect(records[0]?.operationId).toBe(view.result.current.prepared?.input.operationId);
  expect(Object.keys(records[0]!).sort()).toEqual([
    "act",
    "actorId",
    "attemptId",
    "format",
    "installationId",
    "operationId",
    "serviceOrigin",
    "subject",
  ]);
});
it("storage denial is NOT_SENT and a context change during persistence cannot send", async () => {
  vi.spyOn(service, "preview").mockImplementation((prepared) => Promise.resolve(preview(prepared)));
  const execute = vi.spyOn(service, "execute");
  const denied: RecoveryJournal = {
    ...testJournal,
    put: () => Promise.reject(new Error("denied")),
  };
  const refused = closeOperation(denied);
  await reviewClose(refused);
  await act(async () => {
    await refused.result.current.execute();
  });
  await waitFor(() => {
    expect(refused.result.current.notice).toEqual({ kind: "local", reason: "recoveryUnavailable" });
  });
  expect(execute).not.toHaveBeenCalled();
  refused.unmount();
  let commit!: () => Promise<void>;
  const paused: RecoveryJournal = {
    ...testJournal,
    put: (record) =>
      new Promise<void>((resolve) => {
        commit = () => testJournal.put(record).then(resolve);
      }),
  };
  const stale = closeOperation(paused);
  await reviewClose(stale);
  act(() => {
    void stale.result.current.execute();
  });
  await waitFor(() => {
    expect(stale.result.current.phase).toBe("PREPARING");
  });
  stale.rerender({ credential: "other-actor-credential" });
  await act(async () => {
    await commit();
  });
  expect(execute).not.toHaveBeenCalled();
});
it("recovery listing excludes another actor and installation", async () => {
  await testJournal.put({ ...recoveryRecord(), actorId: "10000000-0000-4000-8000-000000000099" });
  await testJournal.put({
    ...recoveryRecord(),
    installationId: "10000000-0000-4000-8000-000000000098",
  });
  await testJournal.put(recoveryRecord());
  const lookup = vi.spyOn(service, "operationGet").mockImplementation((operationId) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId,
        outcome: { kind: "PENDING", data: { finalAt: "2026-11-07T10:10:00.000000Z" } },
      },
    }),
  );
  render(
    <RecoveryProvider>
      <UnresolvedNotice />
    </RecoveryProvider>,
  );
  expect(await screen.findAllByRole("button", { name: "Check now" })).toHaveLength(1);
  await waitFor(() => {
    expect(lookup).toHaveBeenCalledTimes(1);
  });
  expect(lookup.mock.calls[0]?.[0]).toBe(recoveryRecord().operationId);
});

it.each(["switchback", "unmount"] as const)(
  "a committed journal callback after %s cannot dispatch a stale request",
  async (change) => {
    vi.spyOn(service, "preview").mockImplementation((prepared) =>
      Promise.resolve(preview(prepared)),
    );
    const execute = vi.spyOn(service, "execute");
    let commit!: () => Promise<void>;
    const journal: RecoveryJournal = {
      ...testJournal,
      put: (record) =>
        new Promise<void>((resolve) => {
          commit = () => testJournal.put(record).then(resolve);
        }),
    };
    const view = closeOperation(journal);
    await reviewClose(view);
    act(() => {
      void view.result.current.execute();
    });
    await waitFor(() => {
      expect(view.result.current.phase).toBe("PREPARING");
    });
    if (change === "switchback") {
      view.rerender({ credential: "other-actor-credential" });
      view.rerender({ credential: "token" });
    } else {
      view.unmount();
    }
    await act(async () => {
      await commit();
    });
    expect(execute).not.toHaveBeenCalled();
    expect(await testJournal.list(recoveryScope)).toHaveLength(1);
  },
);
