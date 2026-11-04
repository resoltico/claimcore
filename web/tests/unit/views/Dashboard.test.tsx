import { navigate } from "../../../src/routing/navigation";
import { stubWorklistSummary } from "../../support/worklist-summary.fixtures";
import { actorContextInput } from "../../support/inputs";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { wireReply } from "../../support/contract-corpus.fixtures";
import { installationOffers } from "../../support/installation-advice.fixtures";
import { stubDashboardContext } from "../../support/dashboard-context.fixtures";
import { fillRegistrationInputs } from "../../support/registration-inputs.fixtures";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../../../src/views/Dashboard";
import { context, response } from "../../support/service-ui.fixtures";

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});

it("renders public product metadata while an authorized case read is pending", async () => {
  vi.mocked(globalThis.fetch).mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard />);
  expect(screen.queryByText("Loading application…")).toBeNull();
  await waitFor(() => {
    expect(globalThis.fetch).toHaveBeenCalledWith("/api/case.list", expect.anything());
  });
  expect(screen.getByRole("link", { name: "ClaimCore" })).toBeVisible();
});

it("locks navigation and logout while a reviewed mutation is dispatched", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", { offers: context.offers, items: [], nextCursor: null }),
  );
  fetch.mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  await screen.findByRole("button", { name: "Review changes" });
  fillRegistrationInputs({ caseReference: "LOCK-001", incidentDate: "2026-09-09" });
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(document.querySelector('.workspace-sidebar a[href="/cases"]')).toHaveAttribute(
      "aria-disabled",
      "true",
    );
  });
  await user.click(document.querySelector(".account-menu > summary")!);
  expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Back to cases" })).toBeDisabled();
});

it("chooses an advertised workspace only for the default entry route", async () => {
  navigate("/", true);
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: actorContextInput({
          productVersion: "0.10.0",
          businessDate: "2026-09-09",
          timeZone: "Etc/UTC",
          capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
          views: ["AUTHORITY", "OPERATIONS"],
          offers: installationOffers(["REGISTER", "GRANT", "REVOKE", "ENABLE", "DISABLE"]),
        }),
      },
    },
  });
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("authority.list"),
  });
  render(<Dashboard />);
  expect(await screen.findByRole("heading", { name: "Access", level: 1 })).toBeVisible();
  expect(window.location.pathname).toBe("/people");
  expect(screen.queryByRole("heading", { name: "Cases", level: 1 })).toBeNull();
  expect(globalThis.fetch).not.toHaveBeenCalled();
});

beforeEach(() => {
  stubWorklistSummary();
});

it("preserves explicit unavailable deep links and discloses no alternative resource", async () => {
  navigate("/cases?view=recent", true);
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: actorContextInput({
          ...context,
          views: ["AUTHORITY"],
          capabilities: ["READ_AUTHORITY"],
          offers: [],
        }),
      },
    },
  });
  render(<Dashboard />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "The requested resource is unavailable.",
  );
  expect(window.location.pathname + window.location.search).toBe("/cases?view=recent");
  expect(globalThis.fetch).not.toHaveBeenCalled();
});
