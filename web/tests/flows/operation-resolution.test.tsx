import { useOperation } from "../../src/hooks/operation/useOperation";
import { current } from "../support/operation-form.fixtures";
import { act, waitFor, renderHook } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
it("reload recovery resolves identities without resubmitting and clears only the selected attempt", async () => {
  const first = recoveryRecord();
  const second = recoveryRecord("10000000-0000-4000-8000-000000000003");
  await testJournal.put(first);
  await testJournal.put(second);
  const resolve = vi.spyOn(service, "resolve").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { operationId: first.operationId, outcome: { kind: "NOT_ACCEPTED", data: null } },
  });
  const execute = vi.spyOn(service, "execute");
  render(<UnresolvedNotice />);
  const buttons = await screen.findAllByRole("button", { name: "Check unresolved operation" });
  expect(buttons).toHaveLength(2);
  await userEvent.click(buttons[0]!);
  expect(resolve).toHaveBeenCalledExactlyOnceWith(first.operationId, "token", first.installationId);
  expect(execute).not.toHaveBeenCalled();
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([second]);
  });
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
it("the journal commits before a pending submission can be handed off", async () => {
  vi.spyOn(service, "preview").mockResolvedValue(preview());
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
  vi.spyOn(service, "preview").mockResolvedValue(preview());
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
  const resolve = vi.spyOn(service, "resolve");
  render(<UnresolvedNotice />);
  expect(await screen.findAllByRole("button", { name: "Check unresolved operation" })).toHaveLength(
    1,
  );
  expect(resolve).not.toHaveBeenCalled();
});

it.each(["switchback", "unmount"] as const)(
  "a committed journal callback after %s cannot dispatch a stale request",
  async (change) => {
    vi.spyOn(service, "preview").mockResolvedValue(preview());
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
