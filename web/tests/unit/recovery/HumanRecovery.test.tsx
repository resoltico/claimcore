import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { recoveryRecord } from "../../support/recovery.fixtures";
import { RecoveryProvider, useRecovery } from "../../../src/recovery/RecoveryContext";
import { JournalContext } from "../../../src/recovery/JournalContext";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { ScreenView } from "../../../src/ui/ScreenView";
import type { RecoveryJournal } from "../../../src/protocol/recoveryJournal";

afterEach(() => vi.unstubAllGlobals());
const record = recoveryRecord();
const receipt = {
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
      <output data-testid="cleanup">{final?.cleanup?.kind ?? ""}</output>
      <button onClick={() => cache?.invalidate()}>Invalidate observations</button>
    </>
  );
};
const setup = () => {
  const journal: RecoveryJournal = {
    list: vi.fn().mockResolvedValue([record]),
    put: vi.fn().mockResolvedValue(undefined),
    remove: vi.fn().mockRejectedValue(new Error("owned cleanup failure")),
  };
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        new Response(
          JSON.stringify({
            representation: "HUMAN",
            operationId: record.operationId,
            outcome: { kind: "ACCEPTED", byRequester: true, document: receipt },
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
      ),
  );
  render(
    <JournalContext value={journal}>
      <RecoveryProvider>
        <View />
      </RecoveryProvider>
    </JournalContext>,
  );
  return journal;
};
it("retains the complete Core receipt and known acceptance when identity-only journal cleanup fails", async () => {
  const journal = setup();
  await screen.findByText("Core authority outcome Ω");
  await waitFor(() => expect(screen.getByTestId("cleanup")).toHaveTextContent("local"));
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
