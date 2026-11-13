import { humanReadHttpReply as response } from "../../support/human-read-reply.fixtures";
import { timelineDocument } from "../../support/timeline-document.fixtures";
import userEvent from "@testing-library/user-event";
import { within } from "@testing-library/react";
import {
  caseDocumentInput,
  caseIdentityScreen,
  correctionChoices,
} from "../../support/case-document.fixtures";
import { beforeEach, it, expect, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseDetail } from "../../../src/views/CaseDetail";
import { definition } from "../../support/service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("erased cases retain their exact reference and revision but expose no facts or acts", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(
      response("case.get", {
        ...caseDocumentInput(
          { revision: 4, history: "SUMMARY" },
          {
            regions: [
              {
                role: "IDENTITY",
                content: {
                  kind: "ENTRIES",
                  entries: [
                    {
                      key: "caseReference",
                      labelId: "field.caseReference.label",
                      prominence: "PRIMARY",
                      value: { kind: "REFERENCE", value: "ERASED-1" },
                    },
                  ],
                },
              },
              {
                role: "NOTICE",
                content: { kind: "NOTICE", messageId: "lifecycle.ERASED.meaning", tone: "FINAL" },
              },
            ],
          },
        ),
        disclosure: "ERASED",
      }),
    )
    .mockResolvedValueOnce(response("case.timeline", timelineDocument([], null, "SUMMARY")));
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "ERASED-1" }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  expect(await screen.findByText(/Erased\. Only/u)).toBeVisible();
  expect(document.body).toHaveTextContent("ERASED-1");
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
  expect(screen.queryByText("Available acts")).toBeNull();
});

it("shows Core-offered alternatives when an unavailable act has no recommendation", async () => {
  const user = userEvent.setup();
  const current = caseDocumentInput(
    {
      revision: 4,
      history: "NONE",
      tasks: ["case.correct", "case.reopen"],
      correction: correctionChoices,
    },
    {
      regions: [
        ...caseIdentityScreen.regions,
        {
          role: "RECOMMENDATION",
          content: {
            kind: "ACTIONS",
            recommended: null,
            groups: [
              { family: "STEWARDSHIP", acts: ["case.correct"] },
              { family: "BUSINESS", acts: ["case.reopen"] },
            ],
          },
        },
      ],
    },
  );
  vi.mocked(fetch).mockResolvedValueOnce(response("case.get", current));
  const onAct = vi.fn();
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CLOSED-1" }}
      unavailableAct="case.decide"
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={onAct}
    />,
  );
  const callout = await screen.findByRole("alert");
  expect(callout).toHaveTextContent("This action is not currently offered for this case.");
  expect(within(callout).getByRole("button", { name: /Correct case facts/u })).toBeVisible();
  await user.click(within(callout).getByRole("button", { name: /Reopen/u }));
  expect(onAct).toHaveBeenCalledWith(
    expect.objectContaining<{ context: unknown }>({
      context: expect.objectContaining({ revision: 4, tasks: ["case.correct", "case.reopen"] }),
    }),
    "case.reopen",
  );
  expect(callout.querySelector(".primary")).toBeNull();
});
