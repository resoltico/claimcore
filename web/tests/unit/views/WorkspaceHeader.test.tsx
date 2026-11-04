import { expect, it, vi } from "vitest";
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
