import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { expect, it } from "vitest";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { activeRequester as context } from "../../support/human-requester.fixtures";
import { workBasis } from "../../support/inputs";
import { workViewsDocument } from "../../support/view-counts.fixtures";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import {
  WorkspaceViewsProvider,
  useWorkspaceViews,
  WorkspaceViewsStatus,
} from "../../../src/views/WorkspaceViews";
import type { HumanReadDocument_work_views } from "@contracts/client";
const result = (document: HumanReadDocument_work_views) => ({
  kind: "outcome" as const,
  status: 200,
  value: {
    representation: "HUMAN" as const,
    outcome: { kind: "SUCCEEDED" as const, document },
  },
});
const Probe = () => {
  const value = useWorkspaceViews();
  return (
    <output>
      {value.cases.data?.items
        .map(
          (item) => `${item.view}:${item.count.kind === "COUNT" ? item.count.value : "withheld"}`,
        )
        .join(" ") ?? "unverified"}
    </output>
  );
};
const view = (requester: typeof context | null) => (
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
  const cases = mockEndpoint("work.views")
    .mockReturnValueOnce(old)
    .mockResolvedValueOnce(
      result(
        workViewsDocument(
          [
            {
              key: "overdue",
              view: "OVERDUE",
              label: { kind: "MESSAGE", messageId: "view.OVERDUE.label" },
              count: { kind: "COUNT", value: 5 },
            },
          ],
          [],
        ),
      ),
    )
    .mockResolvedValue(
      result(
        workViewsDocument(
          [
            {
              key: "attention",
              view: "NEEDS_ATTENTION",
              label: { kind: "MESSAGE", messageId: "view.NEEDS_ATTENTION.label" },
              count: { kind: "COUNT", value: 9 },
            },
          ],
          [],
        ),
      ),
    );
  const rendered = render(view(context));
  await waitFor(() => {
    expect(cases).toHaveBeenCalledTimes(1);
  });
  rendered.rerender(
    view({
      ...context,
      context: { ...context.context, actorId: "10000000-0000-4000-8000-000000000099" },
    }),
  );
  await waitFor(() => {
    expect(screen.getByRole("status")).toHaveTextContent("OVERDUE:5");
  });
  expect(cases.mock.calls[0]?.[2]?.aborted).toBe(true);
  await act(async () => {
    release(
      result(
        workViewsDocument(
          [
            {
              key: "all",
              view: "ALL",
              label: { kind: "MESSAGE", messageId: "view.ALL.label" },
              count: { kind: "COUNT", value: 777 },
            },
          ],
          [],
        ),
      ),
    );
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
  const cases = mockEndpoint("work.views");
  render(view({ ...context, context: { ...context.context, views: ["PEOPLE"] } }));
  expect(screen.getByRole("status")).toHaveTextContent("unverified");
  expect(cases).not.toHaveBeenCalled();
});

it("exposes a failed snapshot as recoverable metadata rather than zero counts", async () => {
  const cases = mockEndpoint("work.views")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValue(result(workViewsDocument()));
  render(
    <WorkspaceViewsProvider>
      <WorkspaceViewsStatus area="cases" />
      <Probe />
    </WorkspaceViewsProvider>,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "ClaimCore could not be reached. Check your connection.",
  );
  expect(screen.getByRole("status")).toHaveTextContent("unverified");
  await userEvent.setup().click(screen.getByRole("button", { name: "Try again" }));
  await waitFor(() => {
    expect(screen.queryByRole("alert")).toBeNull();
  });
  expect(screen.getByRole("status")).toHaveTextContent("NEEDS_ATTENTION:0");
  expect(cases).toHaveBeenCalledTimes(2);
});

it("publishes case and reminder counts from one request with the same date basis", async () => {
  const read = mockEndpoint("work.views").mockResolvedValue(result(workViewsDocument()));
  const Both = () => {
    const value = useWorkspaceViews();
    return (
      <output>
        {JSON.stringify({ cases: value.cases.data, reminders: value.reminders.data })}
      </output>
    );
  };
  render(
    <RequesterContext value={context}>
      <WorkspaceViewsProvider>
        <Both />
      </WorkspaceViewsProvider>
    </RequesterContext>,
  );
  await waitFor(() => {
    expect(read).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("status")).toHaveTextContent('"CANCELLED"');
  });
  expect(screen.getByRole("status").textContent?.split(JSON.stringify(workBasis))).toHaveLength(3);
});
