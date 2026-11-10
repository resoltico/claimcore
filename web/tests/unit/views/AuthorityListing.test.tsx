import { humanReadReply } from "../../support/human-read-reply.fixtures";
import { workBasis } from "../../support/inputs";
import userEvent from "@testing-library/user-event";
import { act, fireEvent, within } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import type { RegionContent } from "@contracts/client";
import { chooseOption } from "../../support/choice-entry";
import type { ApiResult, ServiceResponse } from "../../../src/api/service";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint, getEndpointMock } from "../../support/endpoint-mocks";
import { AuthorityListing } from "../../../src/views/AuthorityListing";
const actorId = "10000000-0000-4000-8000-000000000001";
const subject = "A long principal subject whose complete value remains accessible";
const displayName = "Authored account name";
type Row = Extract<RegionContent, { kind: "TABLE" }>["rows"][number];
const owner: Row = {
  key: actorId,
  values: [
    {
      kind: "ACTOR",
      value: {
        actorId,
        displayName: { kind: "NAMED", value: displayName },
        principalKind: "HUMAN",
        availability: "NOT_ELIGIBLE",
      },
    },
    { kind: "CODE", vocabulary: "accountState", token: "ACTIVE" },
    { kind: "CODES", vocabulary: "role", tokens: ["OWNER"] },
    { kind: "TEXT", value: "support@example.invalid" },
  ],
};
const people = (rows: ReadonlyArray<Row>, cursor: string | null = null) =>
  humanReadReply("authority.list", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "actor",
                labelId: "field.profile.displayName.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "state",
                labelId: "label.accountState",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "roles",
                labelId: "field.role.label",
                prominence: "SECONDARY",
                isMoney: false,
              },
              {
                key: "contact",
                labelId: "field.profile.contact.label",
                prominence: "SECONDARY",
                isMoney: false,
              },
            ],
            rows,
          },
        },
      ],
    },
    context: { kind: "PEOPLE", actorIds: rows.map((row) => row.key), nextCursor: cursor },
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  });
const setup = (mayReadGrants: boolean, row = owner, cursor: string | null = null) => {
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: people([row], cursor),
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
  setup(false, {
    ...owner,
    values: [
      owner.values[0]!,
      { kind: "CODE", vocabulary: "accountState", token: "DISABLED" },
      { kind: "CODES", vocabulary: "role", tokens: [] },
      owner.values[3]!,
    ],
  });
  const table = await screen.findByRole("table", { name: "People & access" });
  await within(table).findByText(displayName);
  expect(within(table).getAllByRole("cell")).toHaveLength(4);
  expect(within(table).queryByRole("link")).toBeNull();
  expect(within(table).getByText("Disabled", { exact: true })).toBeVisible();
});

it("sends literal name/contact and raw role filters to the server, resets its cursor and rejects a late old page", async () => {
  const grants = mockEndpoint("authority.grants");
  setup(true, owner, "opaque-next");
  await screen.findByText(displayName);
  const listing = getEndpointMock("authority.list");
  const pending = Promise.withResolvers<ApiResult<ServiceResponse<"authority.list">>>();
  listing.mockReturnValueOnce(pending.promise);
  const selected: Row = {
    key: "10000000-0000-4000-8000-000000000002",
    values: [
      {
        kind: "ACTOR",
        value: {
          actorId: "10000000-0000-4000-8000-000000000002",
          displayName: { kind: "NAMED", value: "Server selected result" },
          principalKind: "HUMAN",
          availability: "AVAILABLE",
        },
      },
      { kind: "CODE", vocabulary: "accountState", token: "NO_ROLES" },
      { kind: "CODES", vocabulary: "role", tokens: [] },
      { kind: "TEXT", value: "saved-contact@example.invalid" },
    ],
  };
  listing.mockResolvedValueOnce({ kind: "outcome", status: 200, value: people([selected]) });
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
      value: people([
        {
          ...selected,
          values: [{ kind: "TEXT", value: "OLD_PAGE_CANARY" }, ...selected.values.slice(1)],
        },
      ]),
    });
    await pending.promise;
  });
  expect(screen.queryByText("OLD_PAGE_CANARY")).toBeNull();
  expect(listing).toHaveBeenCalledTimes(3);
  expect(grants).not.toHaveBeenCalled();
});
