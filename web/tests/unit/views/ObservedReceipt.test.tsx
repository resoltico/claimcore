import { ScreenView } from "../../../src/ui/ScreenView";
import type { HumanReceiptDocument } from "@contracts/client";
import { afterEach, expect, it, vi } from "vitest";
import {
  render,
  screen,
  waitFor,
  fireEvent,
  TestSession,
} from "../../support/presentation-test-support";
import { useAcceptedOperation } from "../../../src/hooks/useAcceptedOperation";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";
import { ObservedReceipt } from "../../../src/views/operation/ObservedReceipt";
const operationId = "00000000-0000-4000-8000-000000000777";
const document = (caption: string): HumanReceiptDocument => ({
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "outcome",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: caption },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "RECEIPT",
    recordedFields: [],
    target: { kind: "PEOPLE", actorId: activeRequester.context.actorId },
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
});
const response = (caption: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      operationId,
      outcome: { kind: "ACCEPTED", byRequester: true, document: document(caption) },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
const Feedback = () => {
  const accepted = useAcceptedOperation();
  const cache = useEndpointCache();
  return (
    <>
      <button
        onClick={() => {
          accepted.accept(operationId);
        }}
      >
        Retain accepted identity
      </button>
      <button onClick={() => cache?.invalidate()}>Invalidate observations</button>
      {accepted.operationId === null ? null : (
        <ObservedReceipt key={accepted.operationId} operationId={accepted.operationId} />
      )}
    </>
  );
};
const workspace = (actorId = activeRequester.context.actorId, credential = "owner-a") => (
  <TestSession credential={credential}>
    <RequesterContext
      value={{ ...activeRequester, context: { ...activeRequester.context, actorId } }}
    >
      <Feedback />
    </RequesterContext>
  </TestSession>
);
afterEach(() => vi.unstubAllGlobals());
it("masks invalidated receipts immediately and publishes only the current operation observation", async () => {
  let finish: ((value: Response) => void) | undefined;
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response("Original private receipt"))
    .mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          finish = resolve;
        }),
    );
  vi.stubGlobal("fetch", fetch);
  render(workspace());
  fireEvent.click(screen.getByRole("button", { name: "Retain accepted identity" }));
  expect(await screen.findByText("Original private receipt")).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "Invalidate observations" }));
  expect(screen.queryByText("Original private receipt")).toBeNull();
  await waitFor(() => {
    expect(finish).toBeDefined();
  });
  finish!(response("Current authorized receipt"));
  expect(await screen.findByText("Current authorized receipt")).toBeVisible();
  expect(fetch).toHaveBeenCalledTimes(2);
});
it("does not carry retained receipt identity into another verified actor or credential", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(response("Owner private receipt"));
  vi.stubGlobal("fetch", fetch);
  const view = render(workspace());
  fireEvent.click(screen.getByRole("button", { name: "Retain accepted identity" }));
  expect(await screen.findByText("Owner private receipt")).toBeVisible();
  view.rerender(workspace("00000000-0000-4000-8000-000000000888"));
  expect(screen.queryByText("Owner private receipt")).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(1);
  view.rerender(workspace(activeRequester.context.actorId, "owner-b"));
  expect(screen.queryByText("Owner private receipt")).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("keeps embedded receipt sections distinct from the current resource's landmarks", async () => {
  const receipt: HumanReceiptDocument = {
    ...document(""),
    screen: {
      regions: [
        {
          role: "PAYMENTS",
          titleId: "label.payments",
          content: { kind: "TABLE", columns: [], rows: [] },
        },
      ],
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          representation: "HUMAN",
          operationId,
          outcome: { kind: "ACCEPTED", byRequester: true, document: receipt },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  render(
    <>
      <ScreenView view={receipt.screen} />
      <ObservedReceipt operationId={operationId} />
    </>,
  );
  expect(await screen.findByRole("group", { name: "Payments" })).toBeVisible();
  expect(screen.getAllByRole("region", { name: "Payments" })).toHaveLength(1);
});

it("retries a rate-limited receipt read explicitly without resubmitting the recorded change", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
        { status: 429, headers: { "Content-Type": "application/json" } },
      ),
    )
    .mockResolvedValueOnce(response("Observed after explicit retry"));
  vi.stubGlobal("fetch", fetch);
  render(<ObservedReceipt operationId={operationId} />);
  const retry = await screen.findByRole("button", { name: "Try again" });
  expect(fetch).toHaveBeenCalledTimes(1);
  fireEvent.click(retry);
  expect(await screen.findByText("Observed after explicit retry")).toBeVisible();
  expect(fetch).toHaveBeenCalledTimes(2);
  for (const [url, init] of fetch.mock.calls) {
    expect(url).toBe("/api/operation.get");
    if (typeof init?.body !== "string") {
      throw new Error("Expected a JSON lookup request.");
    }
    expect(JSON.parse(init.body)).toMatchObject({ operationId });
  }
});
