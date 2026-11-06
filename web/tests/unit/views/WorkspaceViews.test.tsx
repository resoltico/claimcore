import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { expect, it } from "vitest";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { context } from "../../support/service-ui.fixtures";
import { caseViewsFixture, reminderViewsFixture } from "../../support/view-counts.fixtures";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import {
  WorkspaceViewsProvider,
  useWorkspaceViews,
  WorkspaceViewsStatus,
} from "../../../src/views/WorkspaceViews";
import type { CaseViews, RequesterState_ACTIVE } from "@contracts/client";
const result = (data: CaseViews) => ({
  kind: "outcome" as const,
  status: 200,
  value: { outcome: { kind: "SUCCEEDED" as const, data } },
});
const Probe = () => {
  const value = useWorkspaceViews();
  return (
    <output>
      {value.cases.data?.items.map((item) => `${item.view}:${item.count}`).join(" ") ??
        "unverified"}
    </output>
  );
};
const view = (requester: RequesterState_ACTIVE | null) => (
  <RequesterContext value={requester}>
    <WorkspaceViewsProvider>
      <Probe />
    </WorkspaceViewsProvider>
  </RequesterContext>
);
it("drops a prior requester's late counts even when the credential is reused and the requester returns", async () => {
  let release!: (value: ReturnType<typeof result>) => void;
  const old = new Promise<ReturnType<typeof result>>((resolve) => {
    release = resolve;
  });
  const cases = mockEndpoint("case.views")
    .mockReturnValueOnce(old)
    .mockResolvedValueOnce(result({ ...caseViewsFixture, items: [{ view: "OVERDUE", count: 5 }] }))
    .mockResolvedValue(
      result({ ...caseViewsFixture, items: [{ view: "NEEDS_ATTENTION", count: 9 }] }),
    );
  mockEndpoint("reminder.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: reminderViewsFixture } },
  });
  const rendered = render(view(context));
  await waitFor(() => {
    expect(cases).toHaveBeenCalledTimes(1);
  });
  rendered.rerender(view({ ...context, actorId: "10000000-0000-4000-8000-000000000099" }));
  await waitFor(() => {
    expect(screen.getByRole("status")).toHaveTextContent("OVERDUE:5");
  });
  expect(cases.mock.calls[0]?.[2]?.aborted).toBe(true);
  await act(async () => {
    release(result({ ...caseViewsFixture, items: [{ view: "ALL", count: 777 }] }));
    await old;
  });
  expect(screen.getByRole("status")).not.toHaveTextContent("777");
  rendered.rerender(view(null));
  expect(screen.getByRole("status")).toHaveTextContent("unverified");
  rendered.rerender(view(context));
  await waitFor(() => {
    expect(screen.getByRole("status")).toHaveTextContent("NEEDS_ATTENTION:9");
  });
  expect(cases).toHaveBeenCalledTimes(3);
});
it("does not request or publish case counts without the case view", () => {
  const cases = mockEndpoint("case.views");
  const reminders = mockEndpoint("reminder.views");
  render(view({ ...context, views: ["AUTHORITY"] }));
  expect(screen.getByRole("status")).toHaveTextContent("unverified");
  expect(cases).not.toHaveBeenCalled();
  expect(reminders).not.toHaveBeenCalled();
});

it("exposes a failed snapshot as recoverable metadata rather than zero counts", async () => {
  const cases = mockEndpoint("case.views")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValue(result(caseViewsFixture));
  mockEndpoint("reminder.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: reminderViewsFixture } },
  });
  render(
    <WorkspaceViewsProvider>
      <WorkspaceViewsStatus area="cases" />
      <Probe />
    </WorkspaceViewsProvider>,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "The local service could not be reached.",
  );
  expect(screen.getByRole("status")).toHaveTextContent("unverified");
  await userEvent.setup().click(screen.getByRole("button", { name: "Try again" }));
  await waitFor(() => {
    expect(screen.queryByRole("alert")).toBeNull();
  });
  expect(screen.getByRole("status")).toHaveTextContent("NEEDS_ATTENTION:0");
  expect(cases).toHaveBeenCalledTimes(2);
});
