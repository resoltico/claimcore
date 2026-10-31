import { act, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { SessionContext } from "../../src/session/SessionContext";
import { JournalContext } from "../../src/recovery/JournalContext";
import { context as requester } from "../support/service-ui.fixtures";
import { UnresolvedNotice } from "../../src/views/operation/UnresolvedNotice";
import { service } from "../../src/api/service";
import { pending } from "../support/operation-hook.fixtures";
import {
  testJournal,
  recoveryRecord,
  recoveryScope,
  clearRecoveryFixtures,
} from "../support/recovery.fixtures";
import type { RecoveryJournal } from "../../src/protocol/recoveryJournal";

afterEach(() => {
  vi.restoreAllMocks();
  clearRecoveryFixtures();
});
const view = (
  context: typeof requester | null,
  credential = "token",
  epoch = 0,
  journal: RecoveryJournal = testJournal,
) => (
  <SessionContext
    value={{
      state: { kind: "authenticated", token: credential, epoch },
      logout: () => Promise.resolve(),
      refresh: () => Promise.resolve(),
    }}
  >
    <JournalContext value={journal}>
      <RequesterContext value={context}>
        <UnresolvedNotice />
      </RequesterContext>
    </JournalContext>
  </SessionContext>
);
const answer = (operationId: string) =>
  ({
    kind: "outcome",
    status: 200,
    value: { operationId, outcome: { kind: "NOT_ACCEPTED", data: null } },
  }) as const;
const buttons = () => screen.findAllByRole("button", { name: "Check unresolved operation" });

it.each([false, true])(
  "a same-identity refresh %s does not strand a pending resolution",
  async (refresh) => {
    const record = recoveryRecord();
    const other = recoveryRecord("10000000-0000-4000-8000-000000000003");
    await testJournal.put(record);
    await testJournal.put(other);
    const gate = pending<Awaited<ReturnType<typeof service.resolve>>>();
    const resolve = vi.spyOn(service, "resolve").mockReturnValue(gate.promise);
    const rendered = render(view(requester));
    await userEvent.click((await buttons())[0]!);
    expect((await buttons())[0]).toBeDisabled();
    if (refresh) {
      rendered.rerender(view({ ...requester }));
    }
    await act(async () => {
      gate.finish(answer(record.operationId));
      await gate.promise;
    });
    await waitFor(async () => {
      expect(await testJournal.list(recoveryScope)).toEqual([other]);
    });
    expect(await buttons()).toHaveLength(1);
    expect((await buttons())[0]).not.toBeDisabled();
    expect(resolve).toHaveBeenCalledTimes(1);
  },
);

it("keeps ownership and busy state through a same-identity refresh during journal removal", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const gate = pending<void>();
  const remove = vi.fn(async (selected: typeof record) => {
    await gate.promise;
    await testJournal.remove(selected);
  });
  const journal = { ...testJournal, remove };
  vi.spyOn(service, "resolve").mockResolvedValue(answer(record.operationId));
  const rendered = render(view(requester, "token", 0, journal));
  await userEvent.click((await buttons())[0]!);
  await waitFor(() => {
    expect(remove).toHaveBeenCalledExactlyOnceWith(record);
  });
  rendered.rerender(view({ ...requester }, "token", 0, journal));
  expect((await buttons())[0]).toBeDisabled();
  await act(async () => {
    gate.finish();
    await gate.promise;
  });
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: "Check unresolved operation" })).toBeNull();
  });
  expect(await testJournal.list(recoveryScope)).toEqual([]);
});

it.each(["actor", "installation", "null", "credential", "epoch"] as const)(
  "a %s switchback rejects the old reply without releasing a newer attempt",
  async (change) => {
    const record = recoveryRecord();
    await testJournal.put(record);
    const old = pending<Awaited<ReturnType<typeof service.resolve>>>();
    const fresh = pending<Awaited<ReturnType<typeof service.resolve>>>();
    const resolve = vi
      .spyOn(service, "resolve")
      .mockReturnValueOnce(old.promise)
      .mockReturnValueOnce(fresh.promise);
    const remove = vi.fn(testJournal.remove);
    const journal = { ...testJournal, remove };
    const rendered = render(view(requester, "token", 0, journal));
    await userEvent.click((await buttons())[0]!);
    const changed =
      change === "null"
        ? null
        : {
            ...requester,
            ...(change === "actor" ? { actorId: "10000000-0000-4000-8000-000000000099" } : {}),
            ...(change === "installation"
              ? { installationId: "90000000-0000-4000-8000-000000000099" }
              : {}),
          };
    rendered.rerender(
      view(
        changed,
        change === "credential" ? "other-token" : "token",
        change === "epoch" ? 1 : 0,
        journal,
      ),
    );
    rendered.rerender(view({ ...requester }, "token", 0, journal));
    expect((await buttons())[0]).not.toBeDisabled();
    await userEvent.click((await buttons())[0]!);
    expect(resolve).toHaveBeenCalledTimes(2);
    await act(async () => {
      old.finish(answer(record.operationId));
      await old.promise;
    });
    expect(remove).not.toHaveBeenCalled();
    expect((await buttons())[0]).toBeDisabled();
    await act(async () => {
      fresh.finish(answer(record.operationId));
      await fresh.promise;
    });
    await waitFor(() => {
      expect(remove).toHaveBeenCalledExactlyOnceWith(record);
    });
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  },
);

it("a delayed removal for the old actor cannot clear or unlock the new actor's attempt", async () => {
  const first = recoveryRecord();
  const second = {
    ...first,
    actorId: "10000000-0000-4000-8000-000000000099",
    operationId: "10000000-0000-4000-8000-000000000098",
  };
  await testJournal.put(first);
  await testJournal.put(second);
  const removed = pending<void>();
  const resolved = pending<Awaited<ReturnType<typeof service.resolve>>>();
  const remove = vi.fn(async (record: typeof first) => {
    if (record.actorId === first.actorId) {
      await removed.promise;
    }
    await testJournal.remove(record);
  });
  const journal = { ...testJournal, remove };
  vi.spyOn(service, "resolve")
    .mockResolvedValueOnce(answer(first.operationId))
    .mockReturnValueOnce(resolved.promise);
  const rendered = render(view(requester, "token", 0, journal));
  await userEvent.click((await buttons())[0]!);
  await waitFor(() => {
    expect(remove).toHaveBeenCalledTimes(1);
  });
  rendered.rerender(view({ ...requester, actorId: second.actorId }, "other-token", 1, journal));
  await userEvent.click((await buttons())[0]!);
  await act(async () => {
    removed.finish();
    await removed.promise;
  });
  expect((await buttons())[0]).toBeDisabled();
  expect(await testJournal.list({ ...recoveryScope, actorId: second.actorId })).toEqual([second]);
  await act(async () => {
    resolved.finish(answer(second.operationId));
    await resolved.promise;
  });
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: "Check unresolved operation" })).toBeNull();
  });
  expect(await testJournal.list({ ...recoveryScope, actorId: second.actorId })).toEqual([]);
});

it.each(["uncertain", "transport-throw", "remove-failure"] as const)(
  "%s retains the owned identity and releases busy state for explicit retry",
  async (failure) => {
    const record = recoveryRecord();
    await testJournal.put(record);
    const resolve = vi.spyOn(service, "resolve");
    if (failure === "transport-throw") {
      resolve.mockRejectedValue(new Error("Synthetic unavailable transport"));
    } else if (failure === "uncertain") {
      resolve.mockResolvedValue({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
      });
    } else {
      resolve.mockResolvedValue(answer(record.operationId));
    }
    const remove = vi
      .fn<RecoveryJournal["remove"]>()
      .mockRejectedValue(new Error("Synthetic storage failure"));
    render(view(requester, "token", 0, { ...testJournal, remove }));
    await userEvent.click((await buttons())[0]!);
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Check unresolved operation" })).not.toBeDisabled();
    });
    expect(await testJournal.list(recoveryScope)).toEqual([record]);
    expect(remove).toHaveBeenCalledTimes(failure === "remove-failure" ? 1 : 0);
    expect(resolve).toHaveBeenCalledTimes(1);
  },
);
