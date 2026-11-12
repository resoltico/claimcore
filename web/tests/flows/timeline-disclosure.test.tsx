import { humanReadReply } from "../support/human-read-reply.fixtures";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { caseDocumentInput, caseIdentityScreen } from "../support/case-document.fixtures";
import { definition } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/views/CaseDetail";
const succeeded = (
  disclosure: "FULL" | "SUMMARY",
): ApiResult<ServiceResponse<"case.timeline">> => ({
  kind: "outcome",
  status: 200,
  value: humanReadReply("case.timeline", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "private",
                labelId: "field.handlingSummary.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
            ],
            rows: [
              {
                key: "0",
                values: [
                  {
                    kind: "TEXT",
                    value:
                      disclosure === "FULL"
                        ? "PRIOR_FULL_PAYLOAD_CANARY"
                        : "These changes are not available to this account.",
                  },
                ],
              },
            ],
          },
        },
      ],
    },
    context: { kind: "PAGE", nextCursor: null, tasks: [] },
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure,
  }),
});
const current = (history: "NONE" | "SUMMARY" | "FULL") =>
  caseDocumentInput(
    { history },
    {
      regions: [
        ...caseIdentityScreen.regions,
        { role: "NEXT_ACTIONS", content: { kind: "TABLE", columns: [], rows: [] } },
      ],
    },
  );
it("removes previous disclosed timeline changes before a downgraded disclosure response arrives", async () => {
  let release!: (value: ApiResult<ServiceResponse<"case.timeline">>) => void;
  const pending = new Promise<ApiResult<ServiceResponse<"case.timeline">>>((resolve) => {
    release = resolve;
  });
  mockEndpoint("case.get")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("case.get", current("FULL")),
    })
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("case.get", current("SUMMARY")),
    });
  const timeline = mockEndpoint("case.timeline")
    .mockResolvedValueOnce(succeeded("FULL"))
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
  expect(await screen.findAllByText("PRIOR_FULL_PAYLOAD_CANARY")).toHaveLength(1);
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
    release(succeeded("SUMMARY"));
    await pending;
  });
  expect(screen.queryByText("PRIOR_FULL_PAYLOAD_CANARY")).toBeNull();
  expect(await screen.findByText("These changes are not available to this account.")).toBeVisible();
});
