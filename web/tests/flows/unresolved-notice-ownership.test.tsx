import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import { act, waitFor } from "@testing-library/react";
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
          <UnresolvedNotice showEmpty />
        </RecoveryProvider>
      </RequesterContext>
    </JournalContext>
  </SessionContext>
);
const final = (operationId: string) =>
  ({
    kind: "outcome",
    status: 200,
    value: { operationId, outcome: { kind: "NOT_ACCEPTED", data: null } },
  }) as const;
const waiting = (operationId: string) =>
  ({
    kind: "outcome",
    status: 200,
    value: {
      operationId,
      outcome: { kind: "PENDING", data: { finalAt: "2026-11-07T10:10:00.000000Z" } },
    },
  }) as const;

it("automatically checks and clears final absence without resubmitting a change", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const get = vi.spyOn(service, "operationGet").mockResolvedValue(final(record.operationId));
  const execute = vi.spyOn(service, "execute");
  render(view(requester));
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  expect(get).toHaveBeenCalledExactlyOnceWith(
    record.operationId,
    "token",
    record.installationId,
    expect.any(AbortSignal),
  );
  expect(execute).not.toHaveBeenCalled();
  expect(
    await screen.findByText("This change was not recorded. You can prepare it again."),
  ).toBeVisible();
});

it("shows the deadline and repeats pending checks until a final answer", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const get = vi
    .spyOn(service, "operationGet")
    .mockResolvedValue(final(record.operationId))
    .mockResolvedValueOnce(waiting(record.operationId));
  render(view(requester));
  expect(
    await screen.findByText(
      "Not confirmed yet. The answer will be final by 2026-11-07T10:10:00.000000Z.",
    ),
  ).toBeVisible();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  await waitFor(
    async () => {
      expect(await testJournal.list(recoveryScope)).toEqual([]);
    },
    { timeout: 2500 },
  );
  expect(get).toHaveBeenCalledTimes(2);
});

it("keeps a pending read owned through an equivalent requester refresh", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const gate = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const get = vi.spyOn(service, "operationGet").mockReturnValue(gate.promise);
  const rendered = render(view(requester));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(1);
  });
  rendered.rerender(view({ ...requester }));
  await act(async () => {
    gate.finish(final(record.operationId));
    await gate.promise;
  });
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  expect(get).toHaveBeenCalledTimes(1);
});

it("aborts an old owner's read and ignores its completion after an account change", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const gate = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const get = vi.spyOn(service, "operationGet").mockReturnValue(gate.promise);
  const rendered = render(view(requester));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(1);
  });
  const signal = get.mock.calls[0]![3]!;
  rendered.rerender(
    view({ ...requester, actorId: "20000000-0000-4000-8000-000000000099" }, "other", 1),
  );
  expect(signal.aborted).toBe(true);
  await act(async () => {
    gate.finish(final(record.operationId));
    await gate.promise;
  });
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  expect(screen.queryByText(record.operationId)).toBeNull();
});

it("an A to B to A transition cannot revive an earlier completion", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const old = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const fresh = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const get = vi
    .spyOn(service, "operationGet")
    .mockReturnValueOnce(old.promise)
    .mockReturnValue(fresh.promise);
  const rendered = render(view(requester));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(1);
  });
  rendered.rerender(view(null, "token", 1));
  rendered.rerender(view(requester, "token", 2));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(2);
  });
  await act(async () => {
    old.finish(final(record.operationId));
    await old.promise;
  });
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  await act(async () => {
    fresh.finish(final(record.operationId));
    await fresh.promise;
  });
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
});

it("unmount aborts a passive read without clearing its saved attempt", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const gate = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const get = vi.spyOn(service, "operationGet").mockReturnValue(gate.promise);
  const rendered = render(view(requester));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(1);
  });
  const signal = get.mock.calls[0]![3]!;
  rendered.unmount();
  expect(signal.aborted).toBe(true);
  await act(async () => {
    gate.finish(final(record.operationId));
    await gate.promise;
  });
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});

it("keeps a final identity when local cleanup fails", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  vi.spyOn(service, "operationGet").mockResolvedValue(final(record.operationId));
  const journal: RecoveryJournal = {
    ...testJournal,
    remove: () => Promise.reject(new Error("storage unavailable")),
  };
  render(view(requester, "token", 0, journal));
  expect(await screen.findByRole("alert")).toHaveTextContent("The outcome is known");
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});
