import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../../support/human-requester.fixtures";
import { navigate } from "../../../src/routing/navigation";
import { stubWorklistSummary } from "../../support/worklist-summary.fixtures";

import { mockEndpoint } from "../../support/endpoint-mocks";
import { wireReply } from "../../support/reply.fixtures";
import { stubDashboardContext } from "../../support/dashboard-context.fixtures";
import { fillRegistrationInputs } from "../../support/registration-inputs.fixtures";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../../../src/views/Dashboard";
import { endpointResponse } from "../../support/transport-response.fixtures";

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
  fetch.mockResolvedValueOnce(endpointResponse("case.list", wireReply("case.list")));
  fetch.mockReturnValueOnce(
    new Promise<Response>(() => {
      /* never settles */
    }),
  );
  render(<Dashboard />);
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  await waitFor(() => {
    expect(location.pathname).toBe("/cases/new");
  });
  await screen.findByRole("button", { name: "Review changes" });
  fillRegistrationInputs({ caseReference: "LOCK-001", incidentDate: "2026-09-09" });
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(document.querySelector('.workspace-sidebar a[href="/cases"]')).toHaveAttribute(
      "aria-disabled",
      "true",
    );
  });
  expect(screen.queryByRole("button", { name: "Sign out" })).toBeNull();
  expect(document.querySelector(".account-menu")?.closest('[aria-hidden="true"]')).not.toBeNull();
  for (const close of screen.getAllByRole("button", { name: "Close" })) {
    expect(close).toBeDisabled();
  }
});

it("chooses an advertised workspace only for the default entry route", async () => {
  navigate("/", true);
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: {
          ...activeRequester,
          actions: installationActions([
            "people.register",
            "people.grant",
            "people.revoke",
            "people.enable",
            "people.disable",
          ]),
          links: requesterLinks(
            ["PEOPLE", "OPERATIONS"],
            ["people.register", "people.grant", "people.revoke", "people.enable", "people.disable"],
          ),
        },
      },
    },
  });
  mockEndpoint("people.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("people.list"),
  });
  mockEndpoint("people.requests").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("people.requests"),
  });
  render(<Dashboard />);
  expect(await screen.findByRole("heading", { name: "People & access", level: 1 })).toBeVisible();
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
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: {
          ...activeRequester,
          actions: installationActions(["people.list"]),
          links: requesterLinks(["PEOPLE"], ["people.list"]),
        },
      },
    },
  });
  render(<Dashboard />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "This item does not exist, or you do not have access to it.",
  );
  expect(window.location.pathname + window.location.search).toBe("/cases?view=recent");
  expect(globalThis.fetch).not.toHaveBeenCalled();
});
