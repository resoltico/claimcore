import { humanReadReply } from "../support/human-read-reply.fixtures";
import { workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, TestSession } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { Dashboard } from "../../src/views/Dashboard";
import { service } from "../../src/api/service";
it("keeps an idle authored draft until explicit consent for navigation and sign out", async () => {
  stubDashboardContext();
  mockEndpoint("case.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("case.list", {
        screen: { regions: [] },
        context: {
          kind: "CASES",
          cases: [],
          tasks: ["case.open"],
          exactCaseId: null,
          nextCursor: null,
        },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  const logout = vi.fn().mockResolvedValue(undefined);
  const user = userEvent.setup();
  render(
    <TestSession logout={logout}>
      <Dashboard />
    </TestSession>,
  );
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  await screen.findByRole("textbox", { name: "Claimant name" });
  fillRegistrationInputs({ caseReference: "DRAFT-CONSENT" });
  const name = screen.getByRole("textbox", { name: "Claimant name" });
  const authored = name.getAttribute("value");
  await user.click(screen.getByRole("button", { name: "Settings" }));
  await screen.findByRole("heading", { name: "Settings", level: 1 });
  expect(screen.queryByRole("dialog")).toBeNull();
  await user.click(screen.getByRole("link", { name: "People & access" }));
  expect(screen.getByRole("dialog")).toBeVisible();
  expect(logout).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  expect(document.querySelector('input[data-field-name="claimantName"]')).toBeNull();
  expect(screen.queryByRole("dialog")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Return to workspace" }));
  expect(await screen.findByRole("textbox", { name: "Claimant name" })).toHaveValue(authored);
  await user.click(screen.getByRole("button", { name: "Settings" }));
  await screen.findByRole("heading", { name: "Settings", level: 1 });
  await user.click(
    screen
      .getAllByRole("button", { name: "Sign out" })
      .find((button) => button.closest("main") !== null)!,
  );
  expect(screen.getByRole("dialog")).toBeVisible();
  expect(logout).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  expect(logout).toHaveBeenCalledOnce();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
