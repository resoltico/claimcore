import { afterEach, expect, it, vi } from "vitest";
import { useCallback } from "react";
import { act, waitFor } from "@testing-library/react";
import type { ServiceResponse } from "../../../src/protocol/types";
import { useEndpointPage } from "../../../src/hooks/useEndpointPage";
import { render, screen } from "../../support/presentation-test-support";

const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const reply = (
  caption: string,
  disclosure: "FULL" | "SUMMARY" = "FULL",
  nextCursor: string | null = null,
) => ({
  representation: "HUMAN",
  outcome: {
    kind: "SUCCEEDED",
    document: {
      screen: {
        regions: [
          {
            role: "ACTIVITY",
            content: {
              kind: "TABLE",
              columns: [
                { key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
              ],
              rows:
                caption === ""
                  ? []
                  : [{ key: caption, values: [{ kind: "TEXT", value: caption }] }],
            },
          },
        ],
      },
      context: { kind: "PAGE", tasks: [], nextCursor },
      basis,
      disclosure,
    },
  },
});
const response = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
const select = (value: ServiceResponse<"case.timeline">) => {
  if (value.outcome.kind !== "SUCCEEDED" || value.outcome.document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  const doc = value.outcome.document;
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows.map((row) => row.key) : [],
    ),
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type Page = NonNullable<ReturnType<typeof select>>;
const disclosure = (page: Page) => page.disclosure;
let current: ReturnType<typeof useEndpointPage<"case.timeline", string, Page>> | undefined;
const Harness = ({ actorId }: { actorId: string }) => {
  const input = useCallback(
    (cursor: string | null) => ({
      target: { kind: "ID" as const, caseId: actorId },
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [actorId],
  );
  const page = useEndpointPage("case.timeline", input, select, undefined, undefined, disclosure);
  current = page;
  return (
    <>
      <output data-testid="items">{page.items.join(",")}</output>
      <output data-testid="error">{page.message?.kind ?? ""}</output>
    </>
  );
};
afterEach(() => {
  vi.unstubAllGlobals();
  current = undefined;
});

it("replaces earlier permissive pages when a successful continuation tightens disclosure", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response(reply("old-full", "FULL", "next")))
    .mockResolvedValueOnce(response(reply("new-summary", "SUMMARY")));
  vi.stubGlobal("fetch", fetch);
  render(<Harness actorId="00000000-0000-4000-8000-000000000001" />);
  await screen.findByText("old-full");
  await act(async () => current!.load("next"));
  expect(screen.getByTestId("items")).toHaveTextContent("new-summary");
  expect(screen.getByTestId("items")).not.toHaveTextContent("old-full");
  expect(current!.views).toHaveLength(1);
  expect(fetch.mock.calls[0]![1].headers).toMatchObject({ "ClaimCore-Representation": "HUMAN" });
});

it("masks prior pages immediately on query identity change and ignores an old continuation", async () => {
  let complete: (value: Response) => void = () => {
    throw new Error("No pending request");
  };
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response(reply("owner-a", "FULL", "next")))
    .mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          complete = resolve;
        }),
    )
    .mockResolvedValueOnce(response(reply("owner-b", "SUMMARY")));
  vi.stubGlobal("fetch", fetch);
  const view = render(<Harness actorId="00000000-0000-4000-8000-000000000001" />);
  await screen.findByText("owner-a");
  let pending: Promise<void> = Promise.resolve();
  act(() => {
    pending = current!.load("next");
  });
  view.rerender(<Harness actorId="00000000-0000-4000-8000-000000000002" />);
  expect(screen.getByTestId("items")).not.toHaveTextContent("owner-a");
  await screen.findByText("owner-b");
  await act(async () => {
    complete(response(reply("late-a")));
    await pending;
  });
  expect(screen.getByTestId("items")).toHaveTextContent("owner-b");
  expect(screen.getByTestId("items")).not.toHaveTextContent("late-a");
});

it("publishes failure feedback for a new identity instead of retaining old pages or loading forever", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(response(reply("owner-a")))
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } }),
        { status: 429, headers: { "Content-Type": "application/json" } },
      ),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(<Harness actorId="00000000-0000-4000-8000-000000000001" />);
  await screen.findByText("owner-a");
  view.rerender(<Harness actorId="00000000-0000-4000-8000-000000000002" />);
  await waitFor(() => expect(screen.getByTestId("error")).toHaveTextContent("diagnostic"));
  expect(current!.loading).toBe(false);
  expect(current!.views).toEqual([]);
});
