import userEvent from "@testing-library/user-event";
import { fireEvent, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ActorReviewItem } from "@contracts/client";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { AuthorityListing } from "../../../src/views/AuthorityListing";
const actorId = "10000000-0000-4000-8000-000000000001";
const subject = "A long principal subject whose complete value remains accessible";
const displayName = "Authored account name";
const setup = (mayReadGrants: boolean, changes: Partial<ActorReviewItem> = {}) => {
  const actor: ActorReviewItem = {
    actor: {
      actorId,
      displayName: { kind: "NAMED", value: displayName },
      principalKind: "HUMAN",
      availability: "NOT_ELIGIBLE",
    },
    contact: "support@example.invalid",
    state: "ACTIVE",
    roles: [{ role: "OWNER", installation: true, caseCount: 0 }],
    principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject },
    ...changes,
  };
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: { items: [actor], nextCursor: null } } },
  });
  const select = vi.fn();
  render(
    <AuthorityListing
      mayReadGrants={mayReadGrants}
      onSelect={select}
      onDisclosureRefused={vi.fn()}
      onDisclosureRestored={vi.fn()}
    />,
  );
  return select;
};
it("shows core account state and raw roles with one native person link and no row grant reads", async () => {
  const grants = mockEndpoint("authority.grants");
  const select = setup(true);
  const table = screen.getByRole("table", { name: "People & access" });
  await within(table).findByText(displayName);
  expect(
    within(table)
      .getAllByRole("columnheader")
      .map((header) => header.textContent),
  ).toEqual(["Person", "Roles", "Access to", "Status"]);
  for (const header of within(table).getAllByRole("columnheader")) {
    expect(header).toHaveAttribute("scope", "col");
  }
  const link = within(table).getByRole("link", {
    name: `Human · ${displayName} Enabled with roles`,
  });
  expect(within(table).getAllByRole("link")).toHaveLength(1);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).getAllByRole("cell")[1]).toHaveTextContent("Owner");
  expect(within(table).getAllByRole("cell")[2]).toHaveTextContent("Installation");
  expect(within(table).getAllByRole("cell")[2]).toHaveTextContent("Case-specific grants: 0");
  expect(within(table).getAllByRole("cell")[3]).toHaveTextContent("Enabled with roles");
  expect(within(table).queryByText("No roles")).toBeNull();
  expect(within(table).getByText("support@example.invalid")).toBeVisible();
  expect(grants).not.toHaveBeenCalled();
  expect(link).toHaveAttribute("href", `/people/${actorId}`);
  expect(link.querySelector("bdi")).toHaveAttribute("title", displayName);
  expect(within(table).queryByText(subject)).toBeNull();
  const user = userEvent.setup();
  await user.tab();
  expect(link).toHaveFocus();
  await user.keyboard("{Enter}");
  expect(select).toHaveBeenCalledWith(actorId);
  select.mockClear();
  fireEvent.click(link, { ctrlKey: true });
  expect(select).not.toHaveBeenCalled();
});
it("keeps disclosed table facts without offering a person link when grants are not readable", async () => {
  setup(false, { state: "DISABLED", roles: [] });
  const table = screen.getByRole("table", { name: "People & access" });
  await within(table).findByText(displayName);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).queryByRole("link")).toBeNull();
  expect(within(table).getByText("Disabled", { exact: true })).toBeVisible();
});
