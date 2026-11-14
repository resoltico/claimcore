import { humanReadReply } from "../support/human-read-reply.fixtures";
import { workBasis } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { Dashboard } from "../../src/views/Dashboard";
it("focuses the selected person dialog and restores its originating control on return", async () => {
  stubDashboardContext();
  mockEndpoint("case.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("case.list", {
        screen: { regions: [] },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  const actor = {
    actor: {
      actorId: "00000000-0000-4000-8000-000000000001",
      displayName: { kind: "NAMED" as const, value: "focus-origin" },
      principalKind: "HUMAN" as const,
      availability: "AVAILABLE" as const,
    },
    contact: null,
    state: "NO_ROLES" as const,
    roles: [],
    principal: {
      kind: "HUMAN" as const,
      issuer: "https://issuer.example/",
      subject: "focus-origin",
    },
  };
  mockEndpoint("people.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.list", {
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
                ],
                rows: [
                  {
                    key: actor.actor.actorId,
                    link: {
                      endpoint: "people.grants",
                      input: { actorId: actor.actor.actorId, limit: 20 },
                    },
                    values: [{ kind: "ACTOR", value: actor.actor }],
                  },
                ],
              },
            },
          ],
        },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  mockEndpoint("people.grants").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.grants", {
        screen: {
          regions: [
            {
              role: "ACCOUNT",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "actor",
                    labelId: "field.profile.displayName.label",
                    prominence: "PRIMARY",
                    value: { kind: "ACTOR", value: actor.actor },
                  },
                ],
              },
            },
          ],
        },
        subject: { kind: "PERSON", actorId: actor.actor.actorId },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  const user = userEvent.setup();
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "People and access" }));
  const origin = (await screen.findAllByRole("link", { name: "focus-origin" }))[0]!;
  await user.click(origin);
  const heading = await screen.findByRole("dialog", { name: "focus-origin" });
  await waitFor(() => {
    expect(heading).toHaveFocus();
  });
  expect(document.querySelectorAll("h1")).toHaveLength(1);
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await waitFor(() => {
    expect(origin).toHaveFocus();
  });
  expect(origin).not.toHaveAttribute("tabindex", "-1");
  expect(screen.getByRole("heading", { name: "People & access", level: 1 })).toBeVisible();
  expect(document.querySelectorAll("h1")).toHaveLength(1);
});
