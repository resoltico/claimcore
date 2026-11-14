import { operationAffordance } from "../support/affordances.fixtures";
import { caseDocumentInput, caseIdentityScreen } from "../support/case-document.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { RoutedContent } from "../../src/views/RoutedContent";
import { service, type CurrentCase } from "../../src/api/service";
const mount = (value: CurrentCase, act: string) => {
  const get = mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("case.get", value),
    },
  });
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const view = render(
    <RoutedContent
      route={{
        screen: "caseAct",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        act,
      }}
      setLocked={vi.fn()}
      committed={vi.fn()}
      refresh={0}
      caseNavigation={{ selectCase: vi.fn(), openCase: vi.fn(), backToCases: vi.fn() }}
    />,
  );
  return { view, get, preview, execute };
};
it("keeps the workspace and withholds an unoffered action without reconstructing state policy", async () => {
  const closed = caseDocumentInput(
    {
      actions: [
        operationAffordance(
          "case.reopen",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 1,
          },
          {},
          undefined,
          {},
        ),
      ],
      history: "NONE",
    },
    {
      regions: [...caseIdentityScreen.regions, ...[]],
    },
  );
  const { view, get, preview, execute } = mount(closed, "case.close");
  expect(
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
      level: 1,
    }),
  ).toBeVisible();
  expect(screen.getByRole("button", { name: "Back to cases" })).toBeVisible();
  expect(view.container.querySelector(".case-action-refusal")).toHaveTextContent(
    "This action is not currently offered for this case.",
  );
  expect(view.container.querySelector(".case-action-refusal button")).toHaveTextContent("Reopen");
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(get).toHaveBeenCalledOnce();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
it("does not manufacture authorization advice when the supplied HUMAN document offers no task", async () => {
  const value = caseDocumentInput({ actions: [], history: "NONE" });
  const { view, preview, execute } = mount(value, "case.decide");
  await screen.findByRole("heading", {
    level: 1,
    name: (_name, element) => element.id === "case-detail-title",
  });
  const callout = view.container.querySelector(".case-action-refusal")!;
  expect(callout).toHaveTextContent("This action is not currently offered for this case.");
  expect(callout).not.toHaveTextContent("Case handler");
  expect(callout).not.toHaveTextContent("Undecided");
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
