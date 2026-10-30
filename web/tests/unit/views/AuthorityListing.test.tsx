import userEvent from "@testing-library/user-event";
import { fireEvent, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { AuthorityListing } from "../../../src/views/AuthorityListing";
const actorId = "10000000-0000-4000-8000-000000000001";
const subject = "A long principal subject whose complete value remains accessible";
const setup = (mayReadGrants: boolean) => {
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          items: [
            {
              actorId,
              principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject },
              enabled: true,
            },
          ],
          nextCursor: null,
        },
      },
    },
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
it("exposes labelled table columns and one native keyboard link per actor", async () => {
  const select = setup(true);
  const table = screen.getByRole("table", { name: "Actors and grants" });
  await within(table).findByText(subject);
  expect(
    within(table)
      .getAllByRole("columnheader")
      .map((header) => header.textContent),
  ).toEqual(["Type", "Subject or client ID", "Enabled", "Grant count"]);
  for (const header of within(table).getAllByRole("columnheader")) {
    expect(header).toHaveAttribute("scope", "col");
  }
  const link = within(table).getByRole("link", { name: `Human · ${subject} Yes` });
  expect(within(table).getAllByRole("link")).toHaveLength(1);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(link).toHaveAttribute("href", `/authority/${actorId}`);
  expect(link.querySelector("bdi")).toHaveAttribute("title", subject);
  const user = userEvent.setup();
  await user.tab();
  expect(link).toHaveFocus();
  await user.keyboard("{Enter}");
  expect(select).toHaveBeenCalledWith(actorId);
  select.mockClear();
  fireEvent.click(link, { ctrlKey: true });
  expect(select).not.toHaveBeenCalled();
});
it("keeps the same table facts without inventing a link when grants are not offered", async () => {
  setup(false);
  const table = screen.getByRole("table", { name: "Actors and grants" });
  await within(table).findByText(subject);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).queryByRole("link")).toBeNull();
});
