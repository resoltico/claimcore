import { operationAffordance } from "../../support/affordances.fixtures";
import {
  caseSubject,
  caseDocumentInput,
  caseIdentityScreen,
  correctionChoices,
} from "../../support/case-document.fixtures";
import { humanReadHttpReply as response } from "../../support/human-read-reply.fixtures";
import { timelineDocument } from "../../support/timeline-document.fixtures";
import userEvent from "@testing-library/user-event";
import { within } from "@testing-library/react";

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
          { subject: { ...caseSubject, revision: 4 }, history: "SUMMARY" },
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
      subject: { ...caseSubject, revision: 4 },
      actions: [
        operationAffordance(
          "case.correct",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 4,
          },
          { "group:registration": "KEEP", "group:decision": "KEEP" },
          undefined,
          {
            "group:registration": correctionChoices.registration,
            "group:decision": correctionChoices.decision,
          },
        ),
        operationAffordance(
          "case.reopen",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 4,
          },
          { "group:registration": "KEEP", "group:decision": "KEEP" },
          undefined,
          {
            "group:registration": correctionChoices.registration,
            "group:decision": correctionChoices.decision,
          },
        ),
      ],
      history: "NONE",
    },
    {
      regions: [...caseIdentityScreen.regions, ...[]],
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
    expect.objectContaining<{ subject: unknown }>({
      subject: expect.objectContaining({ revision: 4 }),
    }),
    "case.reopen",
  );
  expect(callout.querySelector(".primary")).toBeNull();
});
