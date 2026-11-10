import { act } from "@testing-library/react";
import { activeRequester } from "../../support/human-requester.fixtures";
import { DisclosureRefreshDriver } from "../../support/people-drawer.fixtures";
import { humanReadResponse } from "../../support/human-read-response.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { actorRefInput } from "../../support/inputs";
import { Dashboard } from "../../../src/views/Dashboard";
import type { RequesterState } from "@contracts/client";
const inactive: ReadonlyArray<Exclude<RequesterState, { kind: "ACTIVE" }>> = [
  {
    kind: "NOT_REGISTERED",
    issuer: "https://issuer.example/",
    subject: "own-account",
    claims: { name: "IdP hint", email: "own@example.invalid" },
  },
  {
    kind: "NO_ROLES",
    self: actorRefInput({ displayName: { kind: "NAMED", value: "Registered name" } }),
    installationId: "90000000-0000-4000-8000-000000000001",
    businessDate: "2026-11-04",
    timeZone: "Europe/Riga",
  },
  {
    kind: "DISABLED",
    self: actorRefInput({
      displayName: { kind: "NAMED", value: "Disabled name" },
      availability: "DISABLED",
    }),
  },
];
it.each(inactive)(
  "shows caller-owned $kind guidance and performs no resource reads",
  async (requester) => {
    mockEndpoint("context").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadResponse("context", "SUCCEEDED", { requester }),
      },
    });
    const cases = mockEndpoint("work.views");
    const directory = mockEndpoint("authority.list");
    const view = render(<Dashboard />);
    expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent(
      requester.kind === "DISABLED" ? "This account is disabled" : "You don’t have access yet",
    );
    expect(cases).not.toHaveBeenCalled();
    expect(directory).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeVisible();
    if (requester.kind === "NOT_REGISTERED") {
      expect(view.container).toHaveTextContent("own-account");
      expect(view.container).toHaveTextContent("We’ve let the owners know");
      expect(view.container).toHaveTextContent("IdP hint");
    } else {
      expect(view.container).toHaveTextContent(
        requester.kind === "NO_ROLES" ? "Registered name" : "Disabled name",
      );
    }
  },
);

it.each(inactive)(
  "keeps caller-owned $kind guidance visible when Check access cannot reach the service",
  async (requester) => {
    mockEndpoint("context")
      .mockResolvedValueOnce({
        kind: "outcome",
        status: 200,
        value: {
          ...humanReadResponse("context", "SUCCEEDED", { requester }),
        },
      })
      .mockResolvedValue({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
        dispatched: false,
      });
    const cases = mockEndpoint("work.views");
    const directory = mockEndpoint("authority.list");
    render(<Dashboard />);
    await screen.findByRole("heading", { level: 1 });
    await userEvent.setup().click(screen.getByRole("button", { name: "Check access" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "ClaimCore could not be reached. Check your connection.",
    );
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      requester.kind === "DISABLED" ? "This account is disabled" : "You don’t have access yet",
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
      value: humanReadResponse("context", "SUCCEEDED", { requester: inactive[0] }),
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
