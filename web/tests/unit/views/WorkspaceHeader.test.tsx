import { expect, it, vi } from "vitest";
import { within } from "@testing-library/react";
import { render, screen } from "../../support/presentation-test-support";
import { actorContextInput, workBasis } from "../../support/inputs";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { WorkspaceHeader } from "../../../src/views/WorkspaceHeader";

it("shows only supplied own role facts and keeps navigation independent from raw assignments", () => {
  const requester = actorContextInput({
    ...workBasis,
    productVersion: "synthetic",
    roles: [{ role: "OWNER", installation: true, caseCount: 0 }],
    capabilities: [],
    views: [],
    offers: [],
  });
  const header = (
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      capabilities={[]}
      views={[]}
      offers={[]}
      locked={false}
      openCase={vi.fn()}
    />
  );
  const view = render(<RequesterContext value={requester}>{header}</RequesterContext>);
  expect(screen.getByLabelText("Roles")).toHaveTextContent("Owner");
  expect(screen.queryByRole("link", { name: "People & access" })).toBeNull();
  expect(screen.queryByRole("link", { name: "Authority history" })).toBeNull();
  expect(document.body).not.toHaveTextContent(requester.issuer);
  expect(document.body).not.toHaveTextContent(requester.actorId);
  view.rerender(<RequesterContext value={null}>{header}</RequesterContext>);
  expect(screen.queryByLabelText("Roles")).toBeNull();
  expect(document.body).not.toHaveTextContent("Owner");
});

it("keeps the shell search an explicitly secondary, labelled submit and preserves its lock", () => {
  const header = (locked: boolean) => (
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      capabilities={[]}
      views={["CASES"]}
      offers={[]}
      locked={locked}
      openCase={vi.fn()}
    />
  );
  const view = render(header(false));
  const search = screen.getByRole("search", { name: "Search cases" });
  const submit = within(search).getByRole("button", { name: "Search" });
  expect(submit).toHaveAttribute("type", "submit");
  expect(submit.classList.contains("ui-button--secondary")).toBe(true);
  expect(submit).not.toBeDisabled();
  view.rerender(header(true));
  expect(submit).toBeDisabled();
  expect(within(search).getByRole("searchbox", { name: "Search cases" })).toBeDisabled();
});
