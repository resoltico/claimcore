import { operationAffordance } from "../../support/affordances.fixtures";
import type {
  RequestByEndpoint,
  HumanReadDocument_case_get,
  HumanReview,
  HumanReceiptDocument,
} from "@contracts/client";
import { requestAddress, requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, TestSession } from "../../support/presentation-test-support";
import { ReminderQueueWork } from "../../../src/views/work/ReminderQueueWork";
const caseId = "00000000-0000-4000-8000-000000000001";
const reminderId = "00000000-0000-4000-8000-000000000002";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const current: HumanReadDocument_case_get = {
  screen: {
    regions: [],
  },
  subject: { kind: "CASE", caseId, revision: 4, workRevision: 6 },
  actions: [],
  links: [],
  history: "NONE",
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: {
    changes: [],
    unchanged: [],
  },
  document: {
    screen: {
      regions: [],
    },
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "CORE-SELECTED",
      },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "reminder.complete",
        {
          kind: "REMINDER",
          target: { kind: "ID", caseId },
          reminderId,
          expectedRevision: 0,
        },
        {},
        [],
      ),
    ],
    links: [],
    history: null,
    nextCursor: null,
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [
      {
        role: "NOTICE",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "receipt",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: {
                kind: "TEXT",
                value: "Core accepted reminder Ψ",
              },
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const page = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "action",
              labelId: "field.reminderAction.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: reminderId,
              actions: [
                operationAffordance("reminder.complete", {
                  kind: "REMINDER",
                  target: { kind: "ID", caseId },
                  reminderId,
                  expectedRevision: 9,
                }),
              ],
              values: [
                {
                  kind: "TEXT",
                  value: "Core selected reminder Ω",
                },
              ],
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
it("takes one parent read and carries the selected reminder version through review, consent and the supplied accepted receipt", async () => {
  const sent: string[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url, options) =>
    Promise.try(() => {
      let outcome: unknown;
      let operationId: string | undefined;
      if (requestAddress(url).endsWith("reminder.list")) {
        outcome = {
          kind: "SUCCEEDED",
          document: page,
        };
      } else if (requestAddress(url).endsWith("case.get")) {
        outcome = {
          kind: "SUCCEEDED",
          document: current,
        };
      } else if (requestAddress(url).endsWith("operation.get")) {
        const operationGetRequest = JSON.parse(
          requestText(options?.body),
        ) as RequestByEndpoint["operation.get"];
        ({ operationId } = operationGetRequest);
        outcome = {
          kind: "ACCEPTED",
          document: receipt,
          byRequester: true,
        };
      } else {
        const operationExecuteRequest = JSON.parse(
          requestText(options?.body),
        ) as RequestByEndpoint["operation.execute"];
        ({ operationId } = operationExecuteRequest);
        sent.push(requestText(options?.body));
        outcome = requestAddress(url).endsWith("preview")
          ? {
              kind: "REVIEWED",
              review,
            }
          : {
              kind: "ACCEPTED",
              document: receipt,
            };
      }
      return new Response(
        JSON.stringify({
          representation: "HUMAN",
          ...(operationId === undefined
            ? {}
            : {
                operationId,
              }),
          outcome,
        }),
        {
          status: 200,
          headers: {
            "Content-Type": "application/json",
          },
        },
      );
    }),
  );
  vi.stubGlobal("fetch", fetch);
  const view = render(
    <TestSession credential="owner-a">
      <ReminderQueueWork filter={{}} onLockChange={vi.fn()} disclosureAvailable />
    </TestSession>,
  );
  await screen.findByText("Core selected reminder Ω");
  await userEvent.click(
    screen.getByRole("button", {
      name: /complete/iu,
    }),
  );
  await waitFor(() => {
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull();
  });
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await screen.findByText("Core accepted reminder Ψ");
  expect(
    fetch.mock.calls.filter(([url]) => requestAddress(url).endsWith("operation.get")),
  ).toHaveLength(1);
  expect(sent).toHaveLength(2);
  expect(sent[1]).toBe(sent[0]);
  expect(JSON.parse(sent[0]!)).toMatchObject({
    task: "reminder.complete",
    binding: {
      kind: "REMINDER",
      target: {
        kind: "ID",
        caseId,
      },
      reminderId,
      expectedRevision: 9,
    },
    inputs: {},
  });
  expect(fetch.mock.calls.filter(([url]) => requestAddress(url).endsWith("case.get"))).toHaveLength(
    1,
  );
  view.rerender(
    <TestSession credential="owner-b">
      <ReminderQueueWork filter={{}} onLockChange={vi.fn()} disclosureAvailable />
    </TestSession>,
  );
  expect(screen.queryByText("Core accepted reminder Ψ")).not.toBeInTheDocument();
  await screen.findByText("Core selected reminder Ω");
  expect(sent).toHaveLength(2);
});
