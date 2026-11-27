import { humanReadReply } from "../support/human-read-reply.fixtures";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { caseDocumentInput, caseIdentityScreen } from "../support/case-document.fixtures";
import { definition } from "../support/service-ui.fixtures";
import { CaseDetail } from "../../src/features/case/CaseDetail";
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
                joins: null,
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
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
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
  const tree = (reloadSignal: number) => (
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" }}
      reloadSignal={reloadSignal}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  );
  const view = render(tree(0));
  expect(await screen.findAllByText("PRIOR_FULL_PAYLOAD_CANARY")).toHaveLength(1);
  view.rerender(tree(1));
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
