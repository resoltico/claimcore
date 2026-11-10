import { humanReadResponse } from "../../support/human-read-response.fixtures";
import { workBasis } from "../../support/inputs";
import userEvent from "@testing-library/user-event";
import { act, fireEvent, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { ActorReviewItem } from "@contracts/client";
import { chooseOption } from "../../support/choice-entry";
import type { ApiResult, ServiceResponse } from "../../../src/api/service";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint, getEndpointMock } from "../../support/endpoint-mocks";
import { AuthorityListing } from "../../../src/views/AuthorityListing";
const actorId = "10000000-0000-4000-8000-000000000001";
const subject = "A long principal subject whose complete value remains accessible";
const displayName = "Authored account name";
const setup = (
  mayReadGrants: boolean,
  changes: Partial<ActorReviewItem> = {},
  cursor: string | null = null,
) => {
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
    value: {
      ...humanReadResponse("authority.list", "SUCCEEDED", {
        basis: workBasis,
        items: [actor],
        nextCursor: cursor,
      }),
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
it("shows core account state and raw roles with one native person link and no row grant reads", async () => {
  const grants = mockEndpoint("authority.grants");
  const select = setup(true);
  const table = await screen.findByRole("table", { name: "People & access" });
  await within(table).findByText(displayName);
  expect(
    within(table)
      .getAllByRole("columnheader")
      .map((header) => header.textContent),
  ).toEqual(["Name", "Account status", "Role", "Contact details"]);
  for (const header of within(table).getAllByRole("columnheader")) {
    expect(header).toHaveAttribute("scope", "col");
  }
  const link = within(table).getByRole("link", {
    name: displayName,
  });
  expect(within(table).getAllByRole("link")).toHaveLength(1);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).getAllByRole("cell")[2]).toHaveTextContent("Owner");
  expect(within(table).getAllByRole("cell")[1]).toHaveTextContent("Enabled with roles");
  expect(within(table).queryByText("No roles")).toBeNull();
  expect(within(table).getByText("support@example.invalid")).toBeVisible();
  expect(grants).not.toHaveBeenCalled();
  expect(link).toHaveAttribute("href", `/people/${actorId}`);
  expect(link.querySelector("bdi")).not.toBeNull();
  expect(within(table).queryByText(subject)).toBeNull();
  const user = userEvent.setup();
  for (let index = 0; index < 10 && document.activeElement !== link; index += 1) {
    await user.tab();
  }
  expect(link).toHaveFocus();
  await user.keyboard("{Enter}");
  expect(select).toHaveBeenCalledWith(actorId);
  select.mockClear();
  fireEvent.click(link, { ctrlKey: true });
  expect(select).not.toHaveBeenCalled();
});
it("keeps disclosed table facts without offering a person link when grants are not readable", async () => {
  setup(false, { state: "DISABLED", roles: [] });
  const table = await screen.findByRole("table", { name: "People & access" });
  await within(table).findByText(displayName);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).queryByRole("link")).toBeNull();
  expect(within(table).getByText("Disabled", { exact: true })).toBeVisible();
});

it("sends literal name/contact and raw role filters to the server, resets its cursor and rejects a late old page", async () => {
  const grants = mockEndpoint("authority.grants");
  setup(true, {}, "opaque-next");
  await screen.findByText(displayName);
  const listing = getEndpointMock("authority.list");
  const pending = Promise.withResolvers<ApiResult<ServiceResponse<"authority.list">>>();
  listing.mockReturnValueOnce(pending.promise);
  const selected: ActorReviewItem = {
    actor: {
      actorId: "10000000-0000-4000-8000-000000000002",
      displayName: { kind: "NAMED", value: "Server selected result" },
      principalKind: "HUMAN",
      availability: "AVAILABLE",
    },
    principal: {
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "unsearched-principal",
    },
    contact: "saved-contact@example.invalid",
    state: "NO_ROLES",
    roles: [],
  };
  listing.mockResolvedValueOnce({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadResponse("authority.list", "SUCCEEDED", {
        basis: workBasis,
        items: [selected],
        nextCursor: null,
      }),
    },
  });
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "More people" }));
  expect(listing.mock.calls[1]![1]).toEqual({ limit: 50, cursor: "opaque-next" });
  const literal = "Ａ名+س@example.invalid";
  await user.type(screen.getByRole("searchbox", { name: "Search people" }), literal);
  await chooseOption(user, screen.getByRole("combobox", { name: "Role filter" }), "CASE_READER");
  await user.click(screen.getByRole("button", { name: "Search" }));
  await screen.findByText("Server selected result");
  expect(listing.mock.calls[2]![1]).toEqual({ limit: 50, text: literal, role: "CASE_READER" });
  expect(listing.mock.calls[1]![2]?.aborted).toBe(true);
  expect(screen.queryByText(displayName)).toBeNull();
  expect(screen.queryByRole("button", { name: "More people" })).toBeNull();
  await act(async () => {
    pending.resolve({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadResponse("authority.list", "SUCCEEDED", {
          basis: workBasis,
          items: [
            {
              ...selected,
              actor: {
                ...selected.actor,
                displayName: { kind: "NAMED", value: "OLD_PAGE_CANARY" },
              },
            },
          ],
          nextCursor: null,
        }),
      },
    });
    await pending.promise;
  });
  expect(screen.queryByText("OLD_PAGE_CANARY")).toBeNull();
  expect(listing).toHaveBeenCalledTimes(3);
  expect(grants).not.toHaveBeenCalled();
});
