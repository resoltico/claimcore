import userEvent from "@testing-library/user-event";
import { within } from "@testing-library/react";
import { casesBreadcrumb } from "../support/workspace-actions";
import { operationAffordance } from "../support/affordances.fixtures";
import { caseDocumentInput, caseIdentityScreen } from "../support/case-document.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { RoutedContent } from "../../src/shell/RoutedContent";
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
        target: { kind: "REFERENCE", caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" },
        act,
      }}
      setLocked={vi.fn()}
      committed={vi.fn()}
      refresh={0}
      caseNavigation={{
        linkNavigation: vi.fn(),
        selectCase: vi.fn(),
        openCase: vi.fn(),
        openCaseQuery: "",
        openAct: vi.fn(),
        closeAct: vi.fn(),
        backToCases: vi.fn(),
      }}
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
  expect(casesBreadcrumb()).toBeVisible();
  expect(view.container.querySelector(".case-action-refusal")).toHaveTextContent(
    "This action is not currently offered for this case.",
  );
  const user = userEvent.setup();
  const callout = view.container.querySelector<HTMLElement>(".case-action-refusal")!;
  await user.click(within(callout).getByRole("button", { name: "More actions" }));
  expect(screen.getByRole("menuitem", { name: /Reopen/u })).toBeVisible();
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(get).toHaveBeenCalledOnce();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
it("does not manufacture authorization advice when the supplied HUMAN document offers no task", async () => {
  const value = caseDocumentInput({ actions: [], history: "NONE" });
  const { preview, execute } = mount(value, "case.decide");
  await screen.findByRole("heading", {
    level: 1,
    name: (_name, element) => element.id === "case-detail-title",
  });
  const callout = await screen.findByRole("alert");
  expect(callout.classList.contains("case-action-refusal")).toBe(true);
  expect(callout).toHaveTextContent("This action is not currently offered for this case.");
  expect(callout).not.toHaveTextContent("Case handler");
  expect(callout).not.toHaveTextContent("Undecided");
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
