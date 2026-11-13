import { act } from "@testing-library/react";
import { activeRequester } from "../../support/human-requester.fixtures";
import { DisclosureRefreshDriver } from "../../support/people-drawer.fixtures";
import { humanReadReply } from "../../support/human-read-reply.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { Dashboard } from "../../../src/views/Dashboard";
import type { HumanReadDocument_context } from "@contracts/client";
const inactive: ReadonlyArray<HumanReadDocument_context> = [
  {
    context: {
      kind: "REQUESTER",
      state: "NOT_REGISTERED",
      actorId: null,
      installationId: null,
      tasks: [],
      views: [],
      peopleIssuerSeed: null,
    },
    screen: {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "subject",
                labelId: "field.principal.subject.label",
                prominence: "DETAIL",
                value: { kind: "IDENTIFIER", value: "own-account" },
              },
              {
                key: "hint",
                labelId: "label.details",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "IdP hint" },
              },
            ],
          },
        },
      ],
    },
    basis: { kind: "UNAVAILABLE" },
    disclosure: "CURRENT",
  },
  {
    context: {
      kind: "REQUESTER",
      state: "NO_ROLES",
      actorId: "10000000-0000-4000-8000-000000000001",
      installationId: "90000000-0000-4000-8000-000000000001",
      tasks: [],
      views: [],
      peopleIssuerSeed: null,
    },
    screen: {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "self",
                labelId: "label.account",
                prominence: "PRIMARY",
                value: { kind: "PARTY", value: "Registered name" },
              },
            ],
          },
        },
      ],
    },
    basis: { kind: "UNAVAILABLE" },
    disclosure: "CURRENT",
  },
  {
    context: {
      kind: "REQUESTER",
      state: "DISABLED",
      actorId: "10000000-0000-4000-8000-000000000001",
      installationId: null,
      tasks: [],
      views: [],
      peopleIssuerSeed: null,
    },
    screen: {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "self",
                labelId: "label.account",
                prominence: "PRIMARY",
                value: { kind: "PARTY", value: "Disabled name" },
              },
            ],
          },
        },
      ],
    },
    basis: { kind: "UNAVAILABLE" },
    disclosure: "CURRENT",
  },
];
it.each(inactive)(
  "shows caller-owned $context.state guidance and performs no resource reads",
  async (requester) => {
    mockEndpoint("context").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadReply("context", requester),
      },
    });
    const cases = mockEndpoint("work.views");
    const directory = mockEndpoint("people.list");
    const view = render(<Dashboard />);
    expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent(
      requester.context.state === "DISABLED"
        ? "This account is disabled"
        : "You don’t have access yet",
    );
    expect(cases).not.toHaveBeenCalled();
    expect(directory).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeVisible();
    if (requester.context.state === "NOT_REGISTERED") {
      expect(view.container).toHaveTextContent("own-account");
      expect(view.container).toHaveTextContent("We’ve let the owners know");
      expect(view.container).toHaveTextContent("IdP hint");
    } else {
      expect(view.container).toHaveTextContent(
        requester.context.state === "NO_ROLES" ? "Registered name" : "Disabled name",
      );
    }
  },
);

it.each(inactive)(
  "keeps caller-owned $context.state guidance visible when Check access cannot reach the service",
  async (requester) => {
    mockEndpoint("context")
      .mockResolvedValueOnce({
        kind: "outcome",
        status: 200,
        value: {
          ...humanReadReply("context", requester),
        },
      })
      .mockResolvedValue({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
        dispatched: false,
      });
    const cases = mockEndpoint("work.views");
    const directory = mockEndpoint("people.list");
    render(<Dashboard />);
    await screen.findByRole("heading", { level: 1 });
    await userEvent.setup().click(screen.getByRole("button", { name: "Check access" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "ClaimCore could not be reached. Check your connection.",
    );
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      requester.context.state === "DISABLED"
        ? "This account is disabled"
        : "You don’t have access yet",
    );
    expect(screen.getByRole("button", { name: "Sign out" })).toBeVisible();
    expect(screen.queryByText("IdP hint")).toBeNull();
    expect(screen.queryByText("Registered name")).toBeNull();
    expect(screen.queryByText("Disabled name")).toBeNull();
    expect(screen.queryByText("own-account")).toBeNull();
    expect(cases).not.toHaveBeenCalled();
    expect(directory).not.toHaveBeenCalled();
  },
);

it("does not retain caller guidance after an explicit HUMAN refusal", async () => {
  mockEndpoint("context")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("context", inactive[0]!),
    })
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
              action: "ASK_FOR_ACCESS",
            },
          },
        },
      },
    });
  render(<Dashboard />);
  await screen.findByRole("heading", { name: "You don’t have access yet" });
  await userEvent.setup().click(screen.getByRole("button", { name: "Check access" }));
  await screen.findByRole("alert");
  expect(screen.queryByRole("heading", { name: "You don’t have access yet" })).toBeNull();
  expect(screen.queryByText("IdP hint")).toBeNull();
  expect(screen.queryByText("own-account")).toBeNull();
});

it("does not revive active requester presentation or permissions after transport failure", async () => {
  mockEndpoint("context")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: {
          kind: "SUCCEEDED",
          document: {
            ...activeRequester,
            context: { ...activeRequester.context, views: [], tasks: [] },
            screen: {
              regions: [
                {
                  role: "IDENTITY",
                  content: {
                    kind: "ENTRIES",
                    entries: [
                      {
                        key: "self",
                        labelId: "label.account",
                        prominence: "PRIMARY",
                        value: { kind: "TEXT", value: "ACTIVE_PREVIOUS_CANARY" },
                      },
                    ],
                  },
                },
              ],
            },
          },
        },
      },
    })
    .mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
  let invalidate: (() => void) | undefined;
  render(
    <>
      <DisclosureRefreshDriver
        capture={(next) => {
          invalidate = next;
        }}
      />
      <Dashboard />
    </>,
  );
  await screen.findByText("ACTIVE_PREVIOUS_CANARY");
  act(() => {
    invalidate!();
  });
  await screen.findByRole("alert");
  expect(screen.queryByText("ACTIVE_PREVIOUS_CANARY")).toBeNull();
  expect(screen.queryByRole("button", { name: "Check access" })).toBeNull();
});
