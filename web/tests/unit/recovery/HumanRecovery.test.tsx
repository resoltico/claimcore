import { useState } from "react";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";
import type { HumanReceiptDocument } from "@contracts/client";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import {
  render,
  screen,
  waitFor,
  testWorkspaceIdentity,
} from "../../support/presentation-test-support";
import { recoveryRecord } from "../../support/recovery.fixtures";
import { RecoveryProvider, useRecovery } from "../../../src/recovery/RecoveryContext";
import { JournalContext } from "../../../src/recovery/JournalContext";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { ScreenView } from "../../../src/ui/ScreenView";
import type { RecoveryJournal } from "../../../src/protocol/recoveryJournal";

afterEach(() => vi.unstubAllGlobals());
const record = recoveryRecord();
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "opaque-acceptance",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core authority outcome Ω" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "RECEIPT",
    target: { kind: "AUTHORITY", actorId: "00000000-0000-4000-8000-000000000002" },
    caseLink: null,
    refreshCase: null,
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const View = () => {
  const recovery = useRecovery();
  const cache = useEndpointCache();
  const final = recovery.finals[record.attemptId];
  return (
    <>
      {final?.outcome.kind === "RECORDED" ? (
        <ScreenView view={final.outcome.view} timeZone={final.outcome.timeZone} />
      ) : null}
      {final?.outcome.kind === "NOT_RECORDED" && final.outcome.view !== null ? (
        <ScreenView view={final.outcome.view} timeZone="Etc/UTC" />
      ) : null}
      {recovery.presentations[record.attemptId] === undefined ? null : (
        <ScreenView view={recovery.presentations[record.attemptId]!} timeZone="Etc/UTC" />
      )}
      <output data-testid="answer">{JSON.stringify(final)}</output>
      <output data-testid="notice">{recovery.notice?.kind ?? ""}</output>
      <button
        onClick={() => {
          recovery.check(record);
        }}
      >
        Recheck identity
      </button>
      <output data-testid="cleanup">{final?.cleanup?.kind ?? ""}</output>
      <button onClick={() => cache?.invalidate()}>Invalidate observations</button>
    </>
  );
};
const setup = (
  kind: "ACCEPTED" | "NOT_ACCEPTED" | "PENDING" = "ACCEPTED",
  document: HumanReceiptDocument = receipt,
) => {
  const journal: RecoveryJournal = {
    list: vi.fn().mockResolvedValue([record]),
    put: vi.fn().mockResolvedValue(undefined),
    remove: vi.fn().mockRejectedValue(new Error("owned cleanup failure")),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: record.operationId,
          outcome:
            kind === "ACCEPTED"
              ? { kind, byRequester: true, document }
              : {
                  kind,
                  ...(kind === "PENDING" ? { finalAt: "2026-11-09T12:15:00.000000Z" } : {}),
                  document: { ...receipt, context: { kind: "EMPTY" } },
                },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  render(
    <JournalContext value={journal}>
      <RecoveryProvider identity={testWorkspaceIdentity}>
        <View />
      </RecoveryProvider>
    </JournalContext>,
  );
  return journal;
};
it("retains the complete Core receipt and known acceptance when identity-only journal cleanup fails", async () => {
  const journal = setup();
  await screen.findByText("Core authority outcome Ω");
  await waitFor(() => {
    expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
  });
  expect(journal.remove).toHaveBeenCalledWith(record);
  expect(fetch).toHaveBeenCalledWith(
    "/api/operation.get",
    expect.objectContaining({ body: JSON.stringify({ operationId: record.operationId }) }),
  );
});
it("masks a recovered receipt synchronously when the disclosure generation is invalidated", async () => {
  setup();
  await screen.findByText("Core authority outcome Ω");
  await userEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
  expect(screen.queryByText("Core authority outcome Ω")).not.toBeInTheDocument();
});

it("masks pending operation presentation on invalidation while retaining its journal identity", async () => {
  const journal = setup("PENDING");
  await screen.findByText("Core authority outcome Ω");
  await userEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
  expect(screen.queryByText("Core authority outcome Ω")).not.toBeInTheDocument();
  expect(journal.remove).not.toHaveBeenCalled();
});
it.each(["ACCEPTED", "NOT_ACCEPTED"] as const)(
  "keeps known %s through cleanup failure and a later unavailable read",
  async (kind) => {
    setup(kind);
    await screen.findByText("Core authority outcome Ω");
    await waitFor(() => {
      expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
    });
    vi.mocked(fetch).mockRejectedValueOnce(new TypeError("Network unavailable"));
    await userEvent.click(screen.getByRole("button", { name: "Recheck identity" }));
    await waitFor(() => {
      expect(screen.getByTestId("notice")).toHaveTextContent("local");
    });
    expect(screen.getByText("Core authority outcome Ω")).toBeVisible();
    expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
  },
);

it("keeps the explicit Core case link independently of the refresh hint", async () => {
  setup("ACCEPTED", {
    ...receipt,
    context: {
      kind: "RECEIPT",
      target: { kind: "CASE", caseId: "00000000-0000-4000-8000-000000000003", revision: 2 },
      caseLink: "00000000-0000-4000-8000-000000000003",
      refreshCase: null,
    },
  });
  await screen.findByText("Core authority outcome Ω");
  const answer: unknown = JSON.parse(screen.getByTestId("answer").textContent ?? "null");
  expect(answer).toMatchObject({
    outcome: { kind: "RECORDED", caseId: "00000000-0000-4000-8000-000000000003" },
  });
});

it("keeps an in-flight identity lookup through an unverified same-session requester gap", async () => {
  const pending = Promise.withResolvers<Response>();
  const request = vi.fn<typeof fetch>().mockReturnValue(pending.promise);
  vi.stubGlobal("fetch", request);
  let records = [record];
  const journal: RecoveryJournal = {
    list: vi.fn().mockImplementation(() => Promise.resolve(records)),
    put: vi.fn().mockResolvedValue(undefined),
    remove: vi.fn().mockImplementation(() => {
      records = [];
      return Promise.resolve();
    }),
  };
  const Gap = () => {
    const [verified, setVerified] = useState(true);
    return (
      <>
        <button
          onClick={() => {
            setVerified(false);
          }}
        >
          Requester gap
        </button>
        <button
          onClick={() => {
            setVerified(true);
          }}
        >
          Verify requester
        </button>
        <RequesterContext value={verified ? activeRequester : null}>
          <JournalContext value={journal}>
            <RecoveryProvider identity={testWorkspaceIdentity}>
              <View />
            </RecoveryProvider>
          </JournalContext>
        </RequesterContext>
      </>
    );
  };
  render(<Gap />);
  await waitFor(() => {
    expect(request).toHaveBeenCalledTimes(1);
  });
  const signal = request.mock.calls[0]?.[1]?.signal;
  if (signal === undefined || signal === null) {
    throw new Error("The lookup must own a cancellation signal");
  }
  try {
    await userEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
    await userEvent.click(screen.getByRole("button", { name: "Requester gap" }));
    expect(signal.aborted).toBe(false);
  } finally {
    pending.resolve(
      new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId: record.operationId,
          outcome: {
            kind: "NOT_ACCEPTED",
            document: { ...receipt, screen: { regions: [] }, context: { kind: "EMPTY" } },
          },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );
  }
  await waitFor(() => {
    expect(journal.remove).toHaveBeenCalledWith(record);
  });
  await userEvent.click(screen.getByRole("button", { name: "Verify requester" }));
  expect(request).toHaveBeenCalledTimes(1);
});
