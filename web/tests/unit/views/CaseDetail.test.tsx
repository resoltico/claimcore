import { timelinePage } from "../../support/timeline.fixtures";
import userEvent from "@testing-library/user-event";
import { within } from "@testing-library/react";
import { caseAdvice } from "../../support/case-advice.fixtures";
import { caseViewInput } from "../../support/inputs";
import { beforeEach, it, expect, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseDetail } from "../../../src/views/CaseDetail";
import { definition, fields, response } from "../../support/service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("erased cases retain their exact reference and revision but expose no facts or acts", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(
      response(
        "case.get",
        "SUCCEEDED",
        caseViewInput({
          state: { kind: "ERASED" },
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "ERASED-1",
          revision: 4,
          advice: { acts: [], history: "SUMMARY" },
        }),
      ),
    )
    .mockResolvedValueOnce(
      response("case.timeline", "SUCCEEDED", { ...timelinePage([]), caseReference: "ERASED-1" }),
    );
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "ERASED-1" }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  expect(await screen.findByText(/Erased — only/u)).toBeVisible();
  expect(document.body).toHaveTextContent("ERASED-1");
  expect(document.querySelector(".case-fields")).toBeNull();
  expect(screen.queryByText("Available acts")).toBeNull();
});

it("shows Core-offered alternatives when an unavailable act has no recommendation", async () => {
  const user = userEvent.setup();
  const current = caseViewInput({
    caseReference: "CLOSED-1",
    revision: 4,
    state: { kind: "ACTIVE", fields: { ...fields, status: "CLOSED" } },
    advice: { ...caseAdvice(["CORRECT", "REOPEN"]), history: "NONE" },
  });
  vi.mocked(fetch).mockResolvedValueOnce(response("case.get", "SUCCEEDED", current));
  const onAct = vi.fn();
  render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: "CLOSED-1" }}
      unavailableAct={{
        act: "DECIDE",
        reason: { code: "NOT_OFFERED", action: "REVIEW_LATEST", state: "CLOSED" },
      }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={onAct}
    />,
  );
  const callout = await screen.findByRole("alert");
  expect(callout).toHaveTextContent("Case state: Closed");
  expect(within(callout).getByRole("button", { name: /Correct case facts/u })).toBeVisible();
  await user.click(within(callout).getByRole("button", { name: /Reopen/u }));
  expect(onAct).toHaveBeenCalledWith(current, "REOPEN");
  expect(callout.querySelector(".primary")).toBeNull();
});
