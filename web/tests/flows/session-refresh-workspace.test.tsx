import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { SessionState } from "../../src/session/useSession";
import { App } from "../../src/App";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { installationOffers } from "../support/installation-advice.fixtures";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
const session = vi.hoisted(() => ({
  state: { kind: "authenticated", token: "first-token", epoch: 0 } as SessionState,
}));
vi.mock("../../src/session/useSession", () => ({
  useSession: () => ({
    state: session.state,
    logout: () => Promise.resolve(),
    refresh: () => Promise.resolve(),
  }),
}));
it("the persistent App masks an unverified session refresh and preserves the same owner's authored registration", async () => {
  const context = stubDashboardContext();
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
  const preview = vi
    .spyOn(service, "preview")
    .mockReturnValue(Promise.withResolvers<never>().promise);
  const user = userEvent.setup();
  const view = render(<App />);
  await user.click(await screen.findByRole("button", { name: "Open a case" }));
  await screen.findByRole("textbox", { name: "Claimant name" });
  fillRegistrationInputs({
    caseReference: "REFRESH-DRAFT",
    claimantName: "AUTHORED_REFRESH_CANARY",
  });
  const pending = Promise.withResolvers<ApiResult<ServiceResponse<"context">>>();
  const [initial] = context.mock.results;
  if (initial === undefined || initial.type !== "return") {
    throw new Error("The initial context read must return its independent fixture");
  }
  const original = initial.value;
  context.mockReturnValueOnce(pending.promise);
  session.state = { kind: "authenticated", token: "renewed-token", epoch: 1 };
  view.rerender(<App />);
  await waitFor(() => {
    expect(context).toHaveBeenCalledTimes(2);
  });
  expect(document.body).not.toHaveTextContent("AUTHORED_REFRESH_CANARY");
  await act(async () => {
    pending.resolve(await original);
    await pending.promise;
  });
  await user.click(await screen.findByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  const [prepared, credential] = preview.mock.calls[0]!;
  expect(credential).toBe("renewed-token");
  expect(prepared.input).toMatchObject({
    kind: "CASE",
    act: { kind: "OPEN", values: { claimantName: "AUTHORED_REFRESH_CANARY" } },
  });
});
