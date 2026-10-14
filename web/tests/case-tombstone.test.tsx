import { beforeEach, it, expect, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { CaseDetail } from "../src/views/CaseDetail";
import { definition, response } from "./service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("erased cases retain their exact reference and revision but expose no facts or commands", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(
      response("case.get", "SUCCEEDED", {
        tag: "FOUND",
        current: { state: "ERASED", caseReference: "ERASED-1", revision: "4" },
      }),
    )
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", { tag: "FOUND", entries: [], nextCursor: null }),
    );
  render(
    <CaseDetail
      token="csrf"
      caseReference="ERASED-1"
      reloadSignal={0}
      definition={definition.definition}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByText(/Erased — only/u)).toBeVisible();
  expect(document.body).toHaveTextContent("ERASED-1");
  expect(document.querySelector(".case-fields")).toBeNull();
  expect(screen.queryByText("Available commands")).toBeNull();
});
