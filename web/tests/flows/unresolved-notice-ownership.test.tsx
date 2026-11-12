import { absentLookup, pendingLookup } from "../support/operation-lookup.fixtures";
import { RecoveryProvider } from "../../src/recovery/RecoveryContext";
import userEvent from "@testing-library/user-event";
import { useEndpointCache } from "../../src/hooks/EndpointProvider";
import { navigate } from "../../src/routing/navigation";
import type { Screen, HumanReceiptDocument } from "@contracts/client";
import { act, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { SessionContext } from "../../src/session/SessionContext";
import { JournalContext } from "../../src/recovery/JournalContext";
import { activeRequester as requester } from "../support/human-requester.fixtures";
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
const Invalidate = () => {
  const cache = useEndpointCache();
  return (
    <button
      onClick={() => {
        cache?.invalidate();
      }}
    >
      Refresh authoritative reads
    </button>
  );
};
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
        <RecoveryProvider
          identity={
            context === null
              ? null
              : {
                  actorId: context.context.actorId,
                  installationId: context.context.installationId,
                  credential,
                  sessionEpoch: epoch,
                  serviceOrigin: location.origin,
                }
          }
        >
          <UnresolvedNotice showEmpty />
          <Invalidate />
        </RecoveryProvider>
      </RequesterContext>
    </JournalContext>
  </SessionContext>
);

it("automatically checks and clears final absence without resubmitting a change", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const get = vi.spyOn(service, "operationGet").mockResolvedValue(absentLookup(record.operationId));
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
  expect(document.querySelector('[data-state="NOT_RECORDED"]')).toBeVisible();
  expect(screen.queryByRole("button", { name: "Check now" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  expect(
    await screen.findByText("This change was not recorded. You can prepare it again."),
  ).toBeVisible();
});

it("shows the deadline and repeats pending checks until a final answer", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  const get = vi
    .spyOn(service, "operationGet")
    .mockResolvedValue(absentLookup(record.operationId))
    .mockResolvedValueOnce(pendingLookup(record.operationId));
  render(view(requester));
  await screen.findByText("Checking whether the change was recorded. Final answer by:");
  expect(document.querySelector('time[datetime="2026-11-07T10:10:00.000000Z"]')).not.toBeNull();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  expect(document.querySelector('[data-state="CHECKING"]')).toBeVisible();
  expect(screen.queryByRole("link", { name: "Open case" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
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
    gate.finish(absentLookup(record.operationId));
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
    view(
      {
        ...requester,
        context: { ...requester.context, actorId: "20000000-0000-4000-8000-000000000099" },
      },
      "other",
      1,
    ),
  );
  expect(signal.aborted).toBe(true);
  await act(async () => {
    gate.finish(absentLookup(record.operationId));
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
    old.finish(absentLookup(record.operationId));
    await old.promise;
  });
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  await act(async () => {
    fresh.finish(absentLookup(record.operationId));
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
    gate.finish(absentLookup(record.operationId));
    await gate.promise;
  });
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});

it("keeps a final identity when local cleanup fails", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  vi.spyOn(service, "operationGet").mockResolvedValue(absentLookup(record.operationId));
  const journal: RecoveryJournal = {
    ...testJournal,
    remove: () => Promise.reject(new Error("storage unavailable")),
  };
  render(view(requester, "token", 0, journal));
  expect(await screen.findByRole("alert")).toHaveTextContent("The outcome is known");
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
});

const receiptView: Screen = {
  regions: [
    {
      role: "IDENTITY",
      content: {
        kind: "ENTRIES",
        entries: [
          {
            key: "act",
            labelId: "label.action",
            prominence: "PRIMARY",
            value: { kind: "CODE", vocabulary: "act", token: "OPEN" },
          },
          {
            key: "caseReference",
            labelId: "field.caseReference.label",
            prominence: "PRIMARY",
            value: { kind: "REFERENCE", value: "RECOVERY-CASE" },
          },
          {
            key: "recordedAt",
            labelId: "label.recordedAt",
            prominence: "PRIMARY",
            value: { kind: "INSTANT", value: "2026-11-07T10:00:00.000000Z" },
          },
        ],
      },
    },
  ],
};
const savedReceipt: HumanReceiptDocument = {
  screen: receiptView,
  context: {
    kind: "RECEIPT",
    target: { kind: "CASE", caseId: recoveryRecord().operationId, revision: 1 },
    caseLink: recoveryRecord().operationId,
    refreshCase: recoveryRecord().operationId,
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-07T10:00:00.000000Z",
      businessDate: "2026-11-07",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const recorded = (byRequester = true) => ({
  kind: "outcome" as const,
  status: 200,
  value: {
    representation: "HUMAN" as const,
    operationId: recoveryRecord().operationId,
    outcome: { kind: "ACCEPTED" as const, document: savedReceipt, byRequester },
  },
});

it("retains a recorded card with Core facts, time and disclosed case navigation after cleanup", async () => {
  await testJournal.put({ ...recoveryRecord(), act: "VOID" });
  const get = vi.spyOn(service, "operationGet").mockResolvedValue(recorded());
  const execute = vi.spyOn(service, "execute");
  render(view(requester));
  const link = await screen.findByRole("link", { name: "Open case" });
  expect(link).toHaveAttribute(
    "href",
    `/cases/id/${savedReceipt.context.target.kind === "CASE" ? savedReceipt.context.target.caseId : ""}`,
  );
  expect(document.querySelector('[data-state="RECORDED"]')).toBeVisible();
  expect(screen.getByText("RECOVERY-CASE")).toBeVisible();
  expect(screen.getByText("Open a case")).toBeVisible();
  expect(screen.queryByText("Void case record")).toBeNull();
  expect(document.querySelector('time[datetime="2026-11-07T10:00:00.000000Z"]')).toBeVisible();
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  act(() => {
    globalThis.dispatchEvent(new Event("claimcore-recovery-changed"));
  });
  expect(await screen.findByRole("link", { name: "Open case" })).toBeVisible();
  expect(get).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  await userEvent.click(link);
  expect(location.pathname).toBe(link.getAttribute("href"));
  act(() => {
    navigate("/cases");
  });
  expect(execute).not.toHaveBeenCalled();
});

it("does not attribute another actor's accepted identity or display its receipt", async () => {
  await testJournal.put(recoveryRecord());
  vi.spyOn(service, "operationGet").mockResolvedValue(recorded(false));
  render(view(requester));
  await waitFor(() => {
    expect(document.querySelector('[data-state="NOT_RECORDED"]')).toBeVisible();
  });
  expect(screen.queryByText("RECOVERY-CASE")).toBeNull();
  expect(screen.queryByRole("link", { name: "Open case" })).toBeNull();
});

it.each(["WITHHELD", "ERASED"] as const)(
  "offers no case link when Core omits it from a %s receipt",
  async (disclosure) => {
    await testJournal.put(recoveryRecord());
    const reply = recorded();
    vi.spyOn(service, "operationGet").mockResolvedValue({
      ...reply,
      value: {
        ...reply.value,
        outcome: {
          ...reply.value.outcome,
          document: {
            ...savedReceipt,
            disclosure,
            screen: { regions: [] },
            context: { ...savedReceipt.context, caseLink: null, refreshCase: null },
          },
        },
      },
    });
    render(view(requester));
    await waitFor(() => {
      expect(document.querySelector('[data-state="RECORDED"]')).toBeVisible();
    });
    expect(screen.queryByRole("link", { name: "Open case" })).toBeNull();
    expect(screen.queryByText("RECOVERY-CASE")).toBeNull();
  },
);
it("uses the Core navigation handle for a withheld historical receipt without inferring permission from disclosure", async () => {
  await testJournal.put(recoveryRecord());
  const reply = recorded();
  vi.spyOn(service, "operationGet").mockResolvedValue({
    ...reply,
    value: {
      ...reply.value,
      outcome: {
        ...reply.value.outcome,
        document: {
          ...savedReceipt,
          disclosure: "WITHHELD",
          screen: { regions: [] },
          context: { ...savedReceipt.context, refreshCase: null },
        },
      },
    },
  });
  render(view(requester));
  expect(await screen.findByRole("link", { name: "Open case" })).toHaveAttribute(
    "href",
    `/cases/id/${recoveryRecord().operationId}`,
  );
  expect(screen.queryByText("RECOVERY-CASE")).toBeNull();
});

it("keeps a known recorded card through cleanup failure and retries identity cleanup without resending", async () => {
  const record = recoveryRecord();
  await testJournal.put(record);
  vi.spyOn(service, "operationGet").mockResolvedValue(recorded());
  const execute = vi.spyOn(service, "execute");
  const remove = vi
    .fn()
    .mockRejectedValueOnce(new Error("storage unavailable"))
    .mockImplementation((value: typeof record) => testJournal.remove(value));
  render(view(requester, "token", 0, { ...testJournal, remove }));
  expect(await screen.findByRole("alert")).toHaveTextContent("The outcome is known");
  expect(document.querySelector('[data-state="RECORDED"]')).toBeVisible();
  expect(await testJournal.list(recoveryScope)).toEqual([record]);
  await userEvent.click(screen.getByRole("button", { name: "Check now" }));
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  expect(screen.queryByRole("alert")).toBeNull();
  expect(document.querySelectorAll('[data-state="RECORDED"]')).toHaveLength(1);
  expect(execute).not.toHaveBeenCalled();
});

it.each(["actor", "installation", "epoch", "invalidation"])(
  "clears a disclosed result when %s changes",
  async (change) => {
    await testJournal.put(recoveryRecord());
    vi.spyOn(service, "operationGet").mockResolvedValue(recorded());
    const rendered = render(view(requester));
    await screen.findByRole("link", { name: "Open case" });
    await waitFor(async () => {
      expect(await testJournal.list(recoveryScope)).toEqual([]);
    });
    if (change === "invalidation") {
      await userEvent.click(screen.getByRole("button", { name: "Refresh authoritative reads" }));
    } else {
      rendered.rerender(
        view(
          {
            ...requester,
            context: {
              ...requester.context,
              ...(change === "actor" ? { actorId: "20000000-0000-4000-8000-000000000099" } : {}),
              ...(change === "installation"
                ? { installationId: "90000000-0000-4000-8000-000000000099" }
                : {}),
            },
          },
          "token",
          change === "epoch" ? 1 : 0,
        ),
      );
    }
    expect(screen.queryByText("RECOVERY-CASE")).toBeNull();
    expect(screen.queryByRole("link", { name: "Open case" })).toBeNull();
  },
);

it("completes an in-flight identity check across invalidation without surfacing its older receipt", async () => {
  await testJournal.put(recoveryRecord());
  const gate = pending<Awaited<ReturnType<typeof service.operationGet>>>();
  const get = vi.spyOn(service, "operationGet").mockReturnValue(gate.promise);
  render(view(requester));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(1);
  });
  await userEvent.click(screen.getByRole("button", { name: "Refresh authoritative reads" }));
  await act(async () => {
    gate.finish(recorded());
    await gate.promise;
  });
  await waitFor(async () => {
    expect(await testJournal.list(recoveryScope)).toEqual([]);
  });
  expect(get).toHaveBeenCalledTimes(1);
  expect(screen.queryByText("RECOVERY-CASE")).toBeNull();
  expect(screen.queryByRole("link", { name: "Open case" })).toBeNull();
});
