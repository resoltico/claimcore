import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { caseViewInput } from "../support/inputs";
import { timelineEntry, timelinePage } from "../support/timeline.fixtures";
import { fields, definition } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";
const succeeded = <const T,>(data: T) => ({
  kind: "outcome" as const,
  status: 200,
  value: { outcome: { kind: "SUCCEEDED" as const, data } },
});
const current = (history: ReadonlyArray<"SUMMARY" | "FULL">) =>
  caseViewInput({
    caseReference: "CASE-1",
    revision: 1,
    advice: { acts: [], history },
    state: { kind: "ACTIVE", fields },
  });
it("removes previous disclosed timeline changes before a downgraded disclosure response arrives", async () => {
  let release!: (value: ReturnType<typeof succeeded<ReturnType<typeof timelinePage>>>) => void;
  const pending = new Promise<ReturnType<typeof succeeded<ReturnType<typeof timelinePage>>>>(
    (resolve) => {
      release = resolve;
    },
  );
  mockEndpoint("case.get")
    .mockResolvedValueOnce(succeeded(current(["SUMMARY", "FULL"])))
    .mockResolvedValueOnce(succeeded(current(["SUMMARY"])));
  const timeline = mockEndpoint("case.timeline")
    .mockResolvedValueOnce(
      succeeded(
        timelinePage([
          timelineEntry({
            changes: {
              kind: "DISCLOSED",
              items: [
                {
                  kind: "HANDLING_SUMMARY",
                  change: { before: null, after: "PRIOR_FULL_PAYLOAD_CANARY" },
                },
              ],
            },
          }),
        ]),
      ),
    )
    .mockReturnValueOnce(pending);
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CASE-1" }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  expect(await screen.findAllByText("PRIOR_FULL_PAYLOAD_CANARY")).toHaveLength(2);
  const user = userEvent.setup();
  const controls = document.querySelector(".case-reminder-controls")!;
  expect(controls).not.toHaveAttribute("open");
  await user.click(controls.querySelector("summary")!);
  expect(controls).toHaveAttribute("open");
  await user.click(screen.getByRole("button", { name: "Refresh work" }));
  await waitFor(() => {
    expect(timeline).toHaveBeenCalledTimes(2);
  });
  expect(screen.queryByText("PRIOR_FULL_PAYLOAD_CANARY")).toBeNull();
  await act(async () => {
    release(succeeded(timelinePage([timelineEntry({ changes: { kind: "WITHHELD" } })])));
    await pending;
  });
  expect(screen.queryByText("PRIOR_FULL_PAYLOAD_CANARY")).toBeNull();
  expect(await screen.findByText("These changes are not available to this account.")).toBeVisible();
});
