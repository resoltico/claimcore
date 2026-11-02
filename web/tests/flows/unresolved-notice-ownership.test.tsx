import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
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
        <RecoveryProvider>
          <UnresolvedNotice />
        </RecoveryProvider>
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
const buttons = () => screen.findAllByRole("button", { name: "Check operation status" });

it.each([false, true])(
  "a same-identity refresh %s does not strand a pending resolution",
  async (refresh) => {
    const record = recoveryRecord();
    const other = recoveryRecord("10000000-0000-4000-8000-000000000003");
    await testJournal.put(record);
    await testJournal.put(other);
    const gate = pending<Awaited<ReturnType<typeof service.status>>>();
    const resolve = vi.spyOn(service, "status").mockReturnValue(gate.promise);
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
  vi.spyOn(service, "status").mockResolvedValue(answer(record.operationId));
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
    expect(screen.queryByRole("button", { name: "Check operation status" })).toBeNull();
  });
  expect(await testJournal.list(recoveryScope)).toEqual([]);
});

it.each(["actor", "installation", "null", "credential", "epoch"] as const)(
  "a %s switchback rejects the old reply without releasing a newer attempt",
  async (change) => {
    const record = recoveryRecord();
    await testJournal.put(record);
    const old = pending<Awaited<ReturnType<typeof service.status>>>();
    const fresh = pending<Awaited<ReturnType<typeof service.status>>>();
    const resolve = vi
      .spyOn(service, "status")
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
  const resolved = pending<Awaited<ReturnType<typeof service.status>>>();
  const remove = vi.fn(async (record: typeof first) => {
    if (record.actorId === first.actorId) {
      await removed.promise;
    }
    await testJournal.remove(record);
  });
  const journal = { ...testJournal, remove };
  vi.spyOn(service, "status")
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
    expect(screen.queryByRole("button", { name: "Check operation status" })).toBeNull();
  });
  expect(await testJournal.list({ ...recoveryScope, actorId: second.actorId })).toEqual([]);
});

it.each(["uncertain", "transport-throw", "remove-failure"] as const)(
  "%s retains the owned identity and releases busy state for explicit retry",
  async (failure) => {
    const record = recoveryRecord();
    await testJournal.put(record);
    const resolve = vi.spyOn(service, "status");
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
      expect(screen.getByRole("button", { name: "Check operation status" })).not.toBeDisabled();
    });
    expect(await testJournal.list(recoveryScope)).toEqual([record]);
    expect(remove).toHaveBeenCalledTimes(failure === "remove-failure" ? 1 : 0);
    expect(resolve).toHaveBeenCalledTimes(1);
  },
);

it("passive unresolved status retains identity and settlement requires a separate explicit confirmation", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const status = vi.spyOn(service, "status").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { operationId: record.operationId, outcome: { kind: "UNRESOLVED", data: null } },
  });
  const settle = vi.spyOn(service, "resolve").mockResolvedValue(answer(record.operationId));
  render(view(requester));
  await buttons();
  expect(status).not.toHaveBeenCalled();
  expect(settle).not.toHaveBeenCalled();
  await userEvent.click((await buttons())[0]!);
  await screen.findByText(
    "The outcome is still unresolved. This status check has not blocked the operation.",
  );
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  expect(settle).not.toHaveBeenCalled();
  await userEvent.click(screen.getByRole("button", { name: "Settle operation permanently" }));
  expect(
    await screen.findByRole("dialog", { name: "Settle operation permanently" }),
  ).toHaveTextContent("permanently block its operation ID");
  expect(settle).not.toHaveBeenCalled();
  await waitFor(() => {
    expect(screen.getByRole("dialog", { name: "Settle operation permanently" })).toHaveFocus();
  });
  await userEvent.keyboard("{Enter}");
  expect(settle).not.toHaveBeenCalled();
  await userEvent.tab();
  expect(screen.getByRole("button", { name: "Keep checking status" })).toHaveFocus();
  await userEvent.click(screen.getByRole("button", { name: "Keep checking status" }));
  expect(settle).not.toHaveBeenCalled();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  await userEvent.click(screen.getByRole("button", { name: "Settle operation permanently" }));
  await userEvent.click(screen.getByRole("button", { name: "Confirm permanent settlement" }));
  await waitFor(() => {
    expect(settle).toHaveBeenCalledExactlyOnceWith(
      record.operationId,
      "token",
      record.installationId,
    );
  });
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
});

it("an old actor's pending read cannot block or unlock a newer actor's confirmed settlement", async () => {
  const first = recoveryRecord();
  const second = {
    ...recoveryRecord("10000000-0000-4000-8000-000000000003"),
    actorId: "10000000-0000-4000-8000-000000000099",
  };
  await testJournal.put(first);
  await testJournal.put(second);
  const old = pending<Awaited<ReturnType<typeof service.status>>>();
  const final = pending<Awaited<ReturnType<typeof service.resolve>>>();
  vi.spyOn(service, "status").mockReturnValue(old.promise);
  const settle = vi.spyOn(service, "resolve").mockReturnValue(final.promise);
  const rendered = render(view(requester));
  await userEvent.click((await buttons())[0]!);
  rendered.rerender(view({ ...requester, actorId: second.actorId }));
  await waitFor(() => {
    expect(screen.getByRole("button", { name: "Settle operation permanently" })).not.toBeDisabled();
  });
  await userEvent.click(screen.getByRole("button", { name: "Settle operation permanently" }));
  await userEvent.click(screen.getByRole("button", { name: "Confirm permanent settlement" }));
  await waitFor(() => {
    expect(settle).toHaveBeenCalledExactlyOnceWith(
      second.operationId,
      "token",
      second.installationId,
    );
  });
  await act(async () => {
    old.finish(answer(first.operationId));
    await old.promise;
  });
  expect((await buttons())[0]).toBeDisabled();
  expect(await testJournal.list(recoveryScope)).toEqual([first]);
  await act(async () => {
    final.finish(answer(second.operationId));
    await final.promise;
  });
  await waitFor(async () => {
    expect(await testJournal.list(second)).toEqual([]);
  });
  expect(await testJournal.list(recoveryScope)).toEqual([first]);
});

it("an actor switchback cannot revive an earlier unconfirmed settlement intent", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const settle = vi.spyOn(service, "resolve");
  const rendered = render(view(requester));
  await buttons();
  await userEvent.click(screen.getByRole("button", { name: "Settle operation permanently" }));
  await screen.findByRole("dialog", { name: "Settle operation permanently" });
  rendered.rerender(view({ ...requester, actorId: "10000000-0000-4000-8000-000000000099" }));
  rendered.rerender(view({ ...requester }));
  await buttons();
  expect(screen.queryByRole("dialog", { name: "Settle operation permanently" })).toBeNull();
  expect(settle).not.toHaveBeenCalled();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});

it("keeps a known final result visible when local identity cleanup fails", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const remove = vi.fn().mockRejectedValue(new Error("denied"));
  vi.spyOn(service, "status").mockResolvedValue(answer(record.operationId));
  render(view(requester, "token", 0, { ...testJournal, remove }));
  await userEvent.click((await buttons())[0]!);
  await screen.findByText(
    "This operation ID was not accepted and is permanently blocked for this account.",
  );
  await screen.findByText(
    "The outcome is known, but this local attempt could not be cleared. Its identity has been kept; you can check status again.",
  );
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  expect(screen.queryByText("The operation outcome is unknown.")).toBeNull();
});
