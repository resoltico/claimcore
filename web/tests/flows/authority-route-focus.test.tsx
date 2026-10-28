import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { Dashboard } from "../../src/views/Dashboard";
it("focuses the selected actor route h1 and restores its originating control on return", async () => {
  stubDashboardContext();
  mockEndpoint("case.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: { offers: [], items: [], nextCursor: null } } },
  });
  const actor = {
    actorId: "00000000-0000-4000-8000-000000000001",
    principal: {
      kind: "HUMAN" as const,
      issuer: "https://issuer.example/",
      subject: "focus-origin",
    },
    enabled: true,
  };
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: { items: [actor], nextCursor: null } } },
  });
  mockEndpoint("authority.grants").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: { actor, items: [], nextCursor: null } } },
  });
  const user = userEvent.setup();
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "Authority" }));
  const origin = (await screen.findAllByRole("button", { name: "View grants" }))[0]!;
  await user.click(origin);
  const heading = await screen.findByRole("heading", { name: "Actor grants", level: 1 });
  await waitFor(() => {
    expect(heading).toHaveFocus();
  });
  expect(document.querySelectorAll("h1")).toHaveLength(1);
  await user.click(screen.getByRole("button", { name: "Back to actors" }));
  await waitFor(() => {
    expect(origin).toHaveFocus();
  });
  expect(origin).not.toHaveAttribute("tabindex", "-1");
  expect(screen.getByRole("heading", { name: "Authority", level: 1 })).toBeVisible();
  expect(document.querySelectorAll("h1")).toHaveLength(1);
});
