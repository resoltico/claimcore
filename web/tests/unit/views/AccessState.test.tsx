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
        outcome: {
          view: { kind: "RECORD" as const, sections: [] },
          kind: "SUCCEEDED",
          data: { requester },
        },
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
      expect(view.container).not.toHaveTextContent("IdP hint");
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
          outcome: {
            view: { kind: "RECORD" as const, sections: [] },
            kind: "SUCCEEDED",
            data: { requester },
          },
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
    expect(cases).not.toHaveBeenCalled();
    expect(directory).not.toHaveBeenCalled();
  },
);
