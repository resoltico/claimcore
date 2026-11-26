import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { navigate } from "../../src/routing/navigation";
import { App } from "../../src/shell/App";
const session = vi.hoisted(() => ({
  state: { kind: "authenticated", token: "csrf", epoch: 1 },
  refresh: vi.fn(),
  logout: vi.fn(),
}));
vi.mock("../../src/session/useSession", () => ({ useSession: () => session }));
it.each([
  "state=INVALID",
  "state=PENDING&state=COMPLETED",
  "caseId=INVALID",
  "caseId=10000000-0000-4000-8000-000000000001&caseReference=CONFLICTING",
])(
  "keeps malformed reminder selection %s recoverable inside the authenticated app",
  async (query) => {
    stubDashboardContext();
    const reminders = mockEndpoint("reminder.list");
    navigate(`/reminders?${query}`, true);
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Reminders" })).toBeVisible();
    expect(screen.getByRole("alert")).toBeVisible();
    expect(screen.getByRole("button", { name: "Clear filters" })).toBeVisible();
    expect(screen.getByRole("button", { name: /^Account(?: · |$)/u })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Page not found" })).toBeNull();
    expect(reminders).not.toHaveBeenCalled();
  },
);
