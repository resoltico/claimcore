import { effect } from "../../../support/operation-identity.fixtures";
import { requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { ConflictChanges } from "../../../../src/views/operation/ConflictChanges";
import { prepareRequest } from "../../../../src/protocol/preparedRequest";
import type { RevisionConflictSnapshot } from "../../../../src/operation/operationTypes";
import type { HumanDocument } from "@contracts/client";
const document = (
  caption: string,
  disclosure: "FULL" | "SUMMARY",
  cursor: string | null,
): HumanDocument => ({
  screen: {
    regions: [
      {
        role: "ACTIVITY",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caption",
              labelId: "label.details",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: caption,
              values: [
                {
                  kind: "TEXT",
                  value: caption,
                },
              ],
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "CONTINUATION",
    nextCursor: cursor,
  },
  basis: {
    kind: "UNAVAILABLE",
  },
  disclosure,
});
const input = {
  kind: "CASE",
  operationId: "019aa6a0-0000-7000-8000-000000000001",
  target: {
    kind: "REFERENCE",
    caseReference: "Original reference Ω",
  },
  expectedRevision: 7,
  act: {
    kind: "CLOSE",
  },
} as const;
const snapshot: RevisionConflictSnapshot = {
  minimumRevision: 5,
  identity: {
    id: "conflict",
    credential: "csrf",
    target: "selected",
    contextGeneration: 1,
    owner: effect().owner,
  },
  source: {
    prepared: {
      ...prepareRequest("operation.execute", input),
      input,
    },
    submittedRows: {},
  },
  diagnostic: {
    kind: "CONFLICT",
    advice: {
      message: {
        messageId: "ui.conflict.title",
        arguments: [],
      },
      action: "REVIEW_LATEST",
    },
    actualRevision: 8,
    document: document("Core original full history Ω", "FULL", "issued"),
  },
  latest: null,
};
const model = () => ({
  busy: false,
  revalidating: false,
  invalidateDisclosure: vi.fn(),
  revalidateDisclosure: vi.fn(),
  reviewLatest: vi.fn().mockResolvedValue(undefined),
});
const response = (outcome: unknown) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome,
    }),
    {
      status: 200,
      headers: {
        "Content-Type": "application/json",
      },
    },
  );
afterEach(() => vi.unstubAllGlobals());
it("uses the supplied conflict document and replaces full history with a tightened HUMAN continuation", async () => {
  const original = document("Core replacement summary Ψ", "SUMMARY", null);
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
    response({
      kind: "SUCCEEDED",
      document: {
        ...original,
        context: {
          kind: "PAGE",
          tasks: [],
          nextCursor: null,
        },
        basis: {
          kind: "SNAPSHOT",
          observation: {
            observedAt: "2026-11-09T12:00:00.000000Z",
            businessDate: "2026-11-09",
            timeZone: "Etc/UTC",
          },
        },
      },
    }),
  );
  vi.stubGlobal("fetch", fetch);
  const actions = model();
  render(<ConflictChanges model={actions} snapshot={snapshot} />);
  expect(screen.getByText("Core original full history Ω")).toBeVisible();
  await userEvent.click(
    screen.getByRole("button", {
      name: "Load more",
    }),
  );
  await screen.findByText("Core replacement summary Ψ");
  expect(screen.queryByText("Core original full history Ω")).not.toBeInTheDocument();
  expect(JSON.parse(requestText(fetch.mock.calls[0]![1]?.body))).toEqual({
    target: input.target,
    cursor: "issued",
    limit: 50,
  });
  expect(actions.revalidateDisclosure).toHaveBeenCalledTimes(1);
});
it("clears disclosed conflict history on a definite HUMAN rejection without interpreting diagnostic codes", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      response({
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: {
              messageId: "ui.conflict.title",
              arguments: [],
            },
            action: "READ_CURRENT",
          },
        },
      }),
    ),
  );
  render(<ConflictChanges model={model()} snapshot={snapshot} />);
  await userEvent.click(
    screen.getByRole("button", {
      name: "Load more",
    }),
  );
  await screen.findByRole("alert");
  expect(screen.queryByText("Core original full history Ω")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", {
      name: "Load more",
    }),
  ).not.toBeInTheDocument();
});
