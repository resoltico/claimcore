import type { HumanResponseByEndpoint } from "@contracts/client";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { EntrySelection } from "../../../../src/shell/RoutedContent";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { current } from "../../../support/operation-form.fixtures";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import {
  endpointResponse,
  requestAddress,
  requestText,
} from "../../../support/transport-response.fixtures";

afterEach(() => vi.unstubAllGlobals());

it.each(["manual choice", "exact match on a later page"])(
  "keeps %s on its own completion path",
  async (mode) => {
    const exact = mode === "exact match on a later page";
    const target = { kind: "ID", caseId: current.subject.caseId } as const;
    const paymentId = "90000000-0000-4000-8000-000000000001";
    const action = operationAffordance("case.correctPayment", {
      kind: "CASE",
      target,
      expectedRevision: current.subject.revision,
      paymentId,
    });
    const wrongCase = operationAffordance("case.correctPayment", {
      kind: "CASE",
      target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000009" },
      expectedRevision: current.subject.revision,
      paymentId: "90000000-0000-4000-8000-000000000002",
    });
    const page = {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: {
          ...current,
          actions: [],
          screen: {
            regions: [
              {
                role: "NOTES",
                content: {
                  kind: "TABLE",
                  columns: [
                    {
                      key: "note",
                      labelId: "label.notes",
                      prominence: "PRIMARY",
                      joins: null,
                      isMoney: false,
                    },
                  ],
                  rows: [
                    {
                      key: "eligible",
                      values: [{ kind: "TEXT", value: "Eligible entry" }],
                      actions: [action],
                    },
                    { key: "ineligible", values: [{ kind: "TEXT", value: "Ineligible entry" }] },
                    {
                      key: "foreign",
                      values: [{ kind: "TEXT", value: "Foreign entry" }],
                      actions: [wrongCase],
                    },
                  ],
                },
              },
            ],
          },
        },
      },
    } satisfies HumanResponseByEndpoint["case.notes"];
    let requests = 0;
    const fetcher = vi.fn((resource: RequestInfo | URL, init?: RequestInit) => {
      expect(requestAddress(resource)).toMatch(/\/api\/case\.notes$/u);
      requests += 1;
      const first = requests === 1;
      expect(JSON.parse(requestText(init?.body))).toEqual({
        target,
        ...(first ? {} : { cursor: "next-page" }),
      });
      const response =
        exact && first
          ? {
              ...page,
              outcome: {
                ...page.outcome,
                document: {
                  ...page.outcome.document,
                  screen: { regions: [] },
                  nextCursor: "next-page",
                },
              },
            }
          : page;
      return Promise.resolve(endpointResponse("case.notes", response));
    });
    vi.stubGlobal("fetch", fetcher);
    const onExact = vi.fn();
    const onChoose = vi.fn();
    render(
      <EntrySelection
        current={current}
        route={{
          screen: "caseAct",
          target,
          act: "case.correctPayment",
          ...(exact ? { selection: { kind: "selected", id: paymentId } } : {}),
        }}
        declaration={{ from: "case.notes", promptId: "task.case.correctPayment.selectionPrompt" }}
        onExact={onExact}
        onChoose={onChoose}
        onClose={vi.fn()}
        onUnavailable={vi.fn()}
        revalidate={vi.fn()}
      />,
    );
    if (exact) {
      await waitFor(() => {
        expect(onExact).toHaveBeenCalledWith({ current, action });
      });
      expect(onChoose).not.toHaveBeenCalled();
      expect(fetcher).toHaveBeenCalledTimes(2);
      return;
    }
    expect(await screen.findByText("Choose the payment entry to correct.")).toBeVisible();
    expect(await screen.findByText("Eligible entry")).toBeVisible();
    expect(screen.queryByText("Ineligible entry")).not.toBeInTheDocument();
    expect(screen.queryByText("Foreign entry")).not.toBeInTheDocument();
    expect(onExact).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: /^Correct/u }));
    expect(onChoose).toHaveBeenCalledWith(current, action);
    expect(onExact).not.toHaveBeenCalled();
    expect(fetcher).toHaveBeenCalledTimes(1);
  },
);
