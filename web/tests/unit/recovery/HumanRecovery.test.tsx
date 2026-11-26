import { useState } from "react";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";
import type { HumanDocument } from "@contracts/client";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
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
import { EndpointCache } from "../../../src/hooks/endpointCache";
import { ScreenView } from "../../../src/ui/ScreenView";
import type { RecoveryJournal } from "../../../src/protocol/recoveryJournal";
import { humanReadReply } from "../../support/human-read-reply.fixtures";

afterEach(() => vi.unstubAllGlobals());
let record = recoveryRecord();
beforeEach(() => {
  record = { ...recoveryRecord(), operationId: crypto.randomUUID() };
});
const attachmentCaseId = "00000000-0000-4000-8000-000000000003";
const receipt: HumanDocument = {
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
              value: { kind: "TEXT", value: "Core people outcome Ω" },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
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
  const cache = useEndpointCache()!;
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
      <output data-testid="attachment-disclosure">
        {cache.attachmentDisclosure(attachmentCaseId)}
      </output>
      <button
        onClick={() => {
          cache.invalidate();
        }}
      >
        Invalidate observations
      </button>
    </>
  );
};
const setup = (
  kind: "ACCEPTED" | "NOT_ACCEPTED" | "PENDING" = "ACCEPTED",
  document: HumanDocument = receipt,
  requester: unknown = true,
) => {
  const journal: RecoveryJournal = {
    list: vi.fn().mockResolvedValue([record]),
    put: vi.fn().mockResolvedValue("inserted"),
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
              ? { kind, byRequester: requester, document }
              : {
                  kind,
                  ...(kind === "PENDING" ? { finalAt: "2026-11-09T12:15:00.000000Z" } : {}),
                  document: {
                    ...receipt,
                    subject: { kind: "LIST" },
                    actions: [],
                    links: [],
                    history: null,
                    nextCursor: null,
                  },
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
  await screen.findByText("Core people outcome Ω");
  await waitFor(() => {
    expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
  });
  expect(journal.remove).toHaveBeenCalledWith(record);
  expect(fetch).toHaveBeenCalledWith(
    "/api/operation.get",
    expect.objectContaining({ body: JSON.stringify({ operationId: record.operationId }) }),
  );
});
it("uses an accepted withdrawal's disclosure position in identity-only recovery", async () => {
  const journal = setup("ACCEPTED", {
    ...receipt,
    attachmentObservation: { caseId: attachmentCaseId, sequence: 19 },
    disclosure: "WITHHELD",
  });

  await screen.findByText("Core people outcome Ω");
  expect(screen.getByTestId("attachment-disclosure")).toHaveTextContent("19");
  expect(journal.remove).toHaveBeenCalledWith(record);
});

it.each(["receipt", "timeline"] as const)(
  "clears only the older attachment list when a newer %s supplies a sequence",
  async (source) => {
    const cache = new EndpointCache();
    const read = (sequence: number) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: humanReadReply("case.attachments", {
          ...receipt,
          subject: { kind: "LIST" },
          attachmentObservation: { caseId: attachmentCaseId, sequence },
        }),
      } as const);
    await cache.fetch("older", () => read(18));
    cache.observeAttachmentDisclosure(attachmentCaseId, 18);
    await cache.fetch("current", () => read(19));

    const observation = { caseId: attachmentCaseId, sequence: 19 };
    const observe = () =>
      source === "receipt"
        ? cache.observeAttachmentReceipt({ ...receipt, attachmentObservation: observation })
        : cache.observeAttachmentDisclosure(observation.caseId, observation.sequence);
    expect(observe()).toBe(true);
    expect(cache.snapshot("older").previous).toBeNull();
    expect(cache.snapshot("current").previous).not.toBeNull();

    const invalidated = cache.invalidation();
    expect(observe()).toBe(true);
    expect(cache.invalidation()).toBe(invalidated);
  },
);

it("masks a recovered receipt synchronously when the disclosure generation is invalidated", async () => {
  setup();
  await screen.findByText("Core people outcome Ω");
  await userEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
  expect(screen.queryByText("Core people outcome Ω")).not.toBeInTheDocument();
});

it("masks pending operation presentation on invalidation while retaining its journal identity", async () => {
  const journal = setup("PENDING");
  await screen.findByText("Core people outcome Ω");
  await userEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
  expect(screen.queryByText("Core people outcome Ω")).not.toBeInTheDocument();
  expect(journal.remove).not.toHaveBeenCalled();
});
it.each(["ACCEPTED", "NOT_ACCEPTED"] as const)(
  "keeps known %s through cleanup failure and a later unavailable read",
  async (kind) => {
    setup(kind);
    await screen.findByText("Core people outcome Ω");
    await waitFor(() => {
      expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
    });
    vi.mocked(fetch).mockRejectedValueOnce(new TypeError("Network unavailable"));
    await userEvent.click(screen.getByRole("button", { name: "Recheck identity" }));
    await waitFor(() => {
      expect(screen.getByTestId("notice")).toHaveTextContent("local");
    });
    expect(screen.getByText("Core people outcome Ω")).toBeVisible();
    expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
  },
);

it("keeps the explicit Core case link independently of the refresh hint", async () => {
  setup("ACCEPTED", {
    ...receipt,
    subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
    actions: [],
    links: [
      {
        rel: "OPEN_CASE",
        labelId: "field.caseReference.label",
        link: {
          endpoint: "case.get",
          input: { target: { kind: "ID", caseId: "00000000-0000-4000-8000-000000000003" } },
        },
      },
    ],
    history: null,
    nextCursor: null,
  });
  await screen.findByText("Core people outcome Ω");
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
    put: vi.fn().mockResolvedValue("inserted"),
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
            document: {
              ...receipt,
              screen: { regions: [] },
              subject: { kind: "LIST" },
              actions: [],
              links: [],
              history: null,
              nextCursor: null,
            },
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

it("masks a previously recovered receipt when a fresh lookup refuses disclosure", async () => {
  const journal = setup();
  await screen.findByText("Core people outcome Ω");
  await waitFor(() => {
    expect(screen.getByTestId("cleanup")).toHaveTextContent("local");
  });
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        representation: "HUMAN",
        operationId: record.operationId,
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
              action: "ASK_FOR_ACCESS",
            },
          },
        },
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    ),
  );
  await userEvent.click(screen.getByRole("button", { name: "Recheck identity" }));
  await waitFor(() => {
    expect(screen.getByTestId("notice")).toHaveTextContent("human");
  });
  expect(screen.queryByText("Core people outcome Ω")).not.toBeInTheDocument();
  expect(journal.remove).toHaveBeenCalledTimes(1);
});

it.each([null, "false", 0])(
  "keeps recovery journal when byRequester is malformed: %s",
  async (byRequester) => {
    const journal = setup("ACCEPTED", receipt, byRequester);
    await waitFor(() => {
      expect(screen.getByTestId("notice")).toHaveTextContent("invalidHttp");
    });
    expect(journal.remove).not.toHaveBeenCalled();
    expect(screen.getByTestId("answer").textContent).toBe("");
    expect(fetch).toHaveBeenCalledTimes(1);
  },
);
