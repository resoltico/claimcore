import type { HumanResponseByEndpoint } from "@contracts/client";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { EntrySelection } from "../../../../src/shell/RoutedContent";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { current } from "../../../support/operation-form.fixtures";
import { render, screen } from "../../../support/presentation-test-support";
import {
  endpointResponse,
  requestAddress,
  requestText,
} from "../../../support/transport-response.fixtures";

afterEach(() => vi.unstubAllGlobals());

it("loads the declared case query and offers only rows carrying the selected action", async () => {
  const target = { kind: "ID", caseId: current.subject.caseId } as const;
  const action = operationAffordance("case.correctPayment", {
    kind: "CASE",
    target,
    expectedRevision: current.subject.revision,
    paymentId: "90000000-0000-4000-8000-000000000001",
  });
  const page: HumanResponseByEndpoint["case.notes"] = {
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
                ],
              },
            },
          ],
        },
      },
    },
  };
  const fetcher = vi.fn((resource: RequestInfo | URL, init?: RequestInit) => {
    expect(requestAddress(resource)).toMatch(/\/api\/case\.notes$/u);
    expect(JSON.parse(requestText(init?.body))).toEqual({ target });
    return Promise.resolve(endpointResponse("case.notes", page));
  });
  vi.stubGlobal("fetch", fetcher);
  const onReady = vi.fn();
  render(
    <EntrySelection
      current={current}
      route={{ screen: "caseAct", target, act: "case.correctPayment" }}
      declaration={{ from: "case.notes", promptId: "task.case.correctPayment.selectionPrompt" }}
      onReady={onReady}
      onClose={vi.fn()}
      onUnavailable={vi.fn()}
      revalidate={vi.fn()}
    />,
  );
  expect(await screen.findByText("Choose the payment entry to correct.")).toBeVisible();
  expect(await screen.findByText("Eligible entry")).toBeVisible();
  expect(screen.queryByText("Ineligible entry")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^Correct/u }));
  expect(onReady).toHaveBeenCalledWith({ current, action });
  expect(fetcher).toHaveBeenCalledTimes(1);
});
