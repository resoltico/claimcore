import { workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { installationOffers } from "../support/installation-advice.fixtures";
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
      outcome: {
        kind: "SUCCEEDED",
        data: {
          basis: workBasis,
          offers: installationOffers(["OPEN"]),
          items: [],
          nextCursor: null,
        },
      },
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
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  fillRegistrationInputs({ caseReference: "DRAFT-CONSENT" });
  const name = screen.getByRole("textbox", { name: "Claimant name" });
  const authored = name.getAttribute("value");
  await user.click(screen.getByRole("link", { name: "Recovery" }));
  expect(screen.getByRole("dialog")).toBeVisible();
  expect(logout).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  expect(name).toHaveValue(authored);
  expect(screen.queryByRole("dialog")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Sign out" }));
  expect(screen.getByRole("dialog")).toBeVisible();
  expect(logout).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  expect(logout).toHaveBeenCalledOnce();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
