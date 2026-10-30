import { caseViewInput } from "../../support/inputs";
import { beforeEach, it, expect, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { CaseDetail } from "../../../src/views/CaseDetail";
import { definition, response } from "../../support/service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("erased cases retain their exact reference and revision but expose no facts or acts", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(
      response(
        "case.get",
        "SUCCEEDED",
        caseViewInput({
          state: { kind: "ERASED" },
          caseReference: "ERASED-1",
          revision: 4,
          advice: { acts: [], history: ["SUMMARY"] },
        }),
      ),
    )
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", { entries: [], nextCursor: null }),
    );
  render(
    <CaseDetail
      caseReference="ERASED-1"
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
