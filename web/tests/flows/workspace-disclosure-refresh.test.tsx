import { humanReadReply } from "../support/human-read-reply.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import { caseDocumentInput, closeCaseScreen } from "../support/case-document.fixtures";
import type { HumanReadDocument_context, TaskId } from "@contracts/client";
import {
  openPersonEditor,
  chooseAdmissionRole,
  DisclosureRefreshDriver,
} from "../support/people-drawer.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { timelineDocument } from "../support/timeline-document.fixtures";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { workBasis } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { fields } from "../support/service-ui.fixtures";
import { authorityPreview } from "../support/authority-operation.fixtures";
import { current } from "../support/presentation-state.fixtures";
import { useEndpoint } from "../../src/hooks/useEndpoint";
import { navigate } from "../../src/routing/navigation";
import { referencePath } from "../../src/routing/routes";
import { Dashboard } from "../../src/views/Dashboard";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
beforeEach(() => {
  stubViewCounts();
});
const outcome = <const T,>(value: T) => ({ kind: "outcome" as const, status: 200, value });
const unavailable = {
  kind: "REJECTED",
  rejection: {
    kind: "REFUSED",
    advice: {
      message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
      action: "ASK_FOR_ACCESS",
    },
  },
} as const;
type Views = ReadonlyArray<"CASES" | "AUTHORITY" | "OPERATIONS">;
const authorityTasks: ReadonlyArray<TaskId> = [
  "CASE:OPEN",
  "AUTHORITY:ADMIT",
  "query:authorityList",
  "query:authorityGrants",
  "query:authorityHistory",
  "query:authorityRequests",
  "query:authorityRequestsDismiss",
];
const requesterFor = (
  views: Views,
  tasks: ReadonlyArray<TaskId>,
  issuer: string | null,
): HumanReadDocument_context => ({
  ...activeRequester,
  context: {
    ...activeRequester.context,
    views,
    tasks,
    authorityIssuerSeed: issuer,
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
});
const contextFor = (
  views: Views,
  tasks: ReadonlyArray<TaskId>,
  issuer: string | null = null,
): ApiResult<ServiceResponse<"context">> =>
  outcome(humanReadReply("context", requesterFor(views, tasks, issuer)));
const deferred = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const beginUnknownAuthority = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByRole("link", { name: "People & access" }));
  await screen.findByText("PREVIOUS_ACTOR_CANARY");
  await openPersonEditor();
  await waitFor(() => {
    expect(document.querySelector('[name="input:principal.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [
    ["input:principal.subject", "pending-target"],
    ["input:profile.displayName", "Synthetic pending person"],
  ]) {
    await user.type(document.querySelector<HTMLInputElement>(`[name="${name}"]`)!, value!);
  }
  await chooseAdmissionRole();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
};
const authorityReads = () => {
  mockEndpoint("case.list").mockResolvedValue(
    outcome({
      ...humanReadReply("case.list", {
        screen: { regions: [] },
        context: { kind: "CASES", cases: [], tasks: [], exactCaseId: null, nextCursor: null },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    }),
  );
  mockEndpoint("authority.list")
    .mockResolvedValueOnce(
      outcome({
        ...humanReadReply("authority.list", {
          screen: {
            regions: [
              {
                role: "RESULTS",
                content: {
                  kind: "TABLE",
                  columns: [
                    {
                      key: "actor",
                      labelId: "field.profile.displayName.label",
                      prominence: "PRIMARY",
                      isMoney: false,
                    },
                  ],
                  rows: [
                    {
                      key: "00000000-0000-4000-8000-000000000001",
                      values: [
                        {
                          kind: "ACTOR",
                          value: {
                            actorId: "00000000-0000-4000-8000-000000000001",
                            displayName: { kind: "NAMED", value: "PREVIOUS_ACTOR_CANARY" },
                            principalKind: "HUMAN",
                            availability: "AVAILABLE",
                          },
                        },
                      ],
                    },
                  ],
                },
              },
            ],
          },
          context: {
            kind: "PEOPLE",
            actorIds: ["00000000-0000-4000-8000-000000000001"],
            nextCursor: null,
          },
          basis: { kind: "SNAPSHOT", observation: workBasis },
          disclosure: "CURRENT",
        }),
      }),
    )
    .mockResolvedValue(outcome({ representation: "HUMAN", outcome: unavailable }));
};
it("retains the exact uncertain authority request when delayed context removes its navigation view", async () => {
  const delayed = deferred<ApiResult<ServiceResponse<"context">>>();
  const metadata = mockEndpoint("context")
    .mockResolvedValueOnce(
      contextFor(["CASES", "AUTHORITY", "OPERATIONS"], authorityTasks, "https://issuer.example/"),
    )
    .mockReturnValue(delayed.promise);
  authorityReads();
  authorityPreview();
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    })
    .mockImplementation((prepared) =>
      Promise.resolve(
        outcome({
          representation: "HUMAN",
          operationId: prepared.input.operationId,
          outcome: unavailable,
        }),
      ),
    );
  const user = userEvent.setup();
  render(<Dashboard />);
  await beginUnknownAuthority(user);
  const original = execute.mock.calls[0]![0].bytes;
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(metadata.mock.calls.length).toBeGreaterThan(1);
  });
  await act(async () => {
    delayed.finish(contextFor(["CASES", "OPERATIONS"], ["CASE:OPEN"]));
    await delayed.promise;
  });
  expect(screen.getByRole("heading", { name: "People & access", level: 1 })).toBeVisible();
  expect(screen.queryByRole("link", { name: "People & access" })).toBeNull();
  expect(document.body).not.toHaveTextContent("PREVIOUS_ACTOR_CANARY");
  await user.click(screen.getByRole("button", { name: "Continue current request" }));
  await user.click(await screen.findByRole("button", { name: "Send again" }));
  expect(execute.mock.calls.map(([prepared]) => prepared.bytes)).toEqual([
    original,
    original,
    original,
  ]);
});
const RefreshDisclosure = () => {
  const context = useEndpoint("context", {});
  return (
    <button
      type="button"
      onClick={() => {
        context.reload();
      }}
    >
      Refresh disclosure
    </button>
  );
};
it("keeps the requested case route while an actual read refusal removes every current fact", async () => {
  mockEndpoint("context")
    .mockResolvedValueOnce(
      contextFor(["CASES", "AUTHORITY", "OPERATIONS"], authorityTasks, "https://issuer.example/"),
    )
    .mockResolvedValueOnce(contextFor(["OPERATIONS"], []))
    .mockResolvedValue(
      contextFor(["CASES", "AUTHORITY", "OPERATIONS"], authorityTasks, "https://issuer.example/"),
    );
  const visible = caseDocumentInput(
    { caseId: current.context.caseId, tasks: ["CASE:CLOSE"], history: "FULL" },
    {
      regions: [
        ...closeCaseScreen.regions,
        {
          role: "FACTS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "claimantName",
                labelId: "field.claimantName.label",
                prominence: "PRIMARY",
                value: { kind: "PARTY", value: "CURRENT_FACT_CANARY" },
              },
            ],
          },
        },
      ],
    },
  );
  const caseRead = mockEndpoint("case.get")
    .mockResolvedValueOnce(
      outcome({
        ...humanReadReply("case.get", visible),
      }),
    )
    .mockResolvedValue(outcome({ representation: "HUMAN", outcome: unavailable }));
  mockEndpoint("case.timeline").mockResolvedValue(
    outcome({
      ...humanReadReply("case.timeline", timelineDocument()),
    }),
  );
  navigate(referencePath(fields.caseReference));
  const user = userEvent.setup();
  render(
    <>
      <Dashboard />
      <RefreshDisclosure />
    </>,
  );
  await waitFor(() => {
    expect(document.body).toHaveTextContent("CURRENT_FACT_CANARY");
  });
  await user.click(screen.getByRole("button", { name: "Refresh disclosure" }));
  await waitFor(() => {
    expect(screen.queryByRole("link", { name: "Cases" })).toBeNull();
  });
  expect(document.body).not.toHaveTextContent("CURRENT_FACT_CANARY");
  expect(caseRead).toHaveBeenCalledOnce();
  expect(screen.getByRole("heading", { name: "Case detail", level: 1 })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Refresh disclosure" }));
  await waitFor(() => {
    expect(caseRead).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("alert")).toBeVisible();
  });
  expect(
    screen.getByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
      level: 1,
    }),
  ).toBeVisible();
  expect(document.body).not.toHaveTextContent("CURRENT_FACT_CANARY");
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
});

it.each(["NO_ROLES", "DISABLED"] as const)(
  "retains same-requester uncertain bytes through %s without replay and restores the draft after access returns",
  async (kind) => {
    const active = contextFor(
      ["CASES", "AUTHORITY", "OPERATIONS"],
      authorityTasks,
      "https://issuer.example/",
    );
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.document.context.state !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const self = active.value.outcome.document.context;
    const inactive: HumanReadDocument_context = {
      screen: { regions: [] },
      context: {
        kind: "REQUESTER",
        state: kind,
        actorId: self.actorId,
        installationId: kind === "NO_ROLES" ? self.installationId : null,
        tasks: [],
        views: [],
        authorityIssuerSeed: null,
      },
      basis: { kind: "UNAVAILABLE" },
      disclosure: "CURRENT",
    };
    mockEndpoint("context")
      .mockResolvedValueOnce(active)
      .mockResolvedValueOnce(
        outcome({
          ...humanReadReply("context", inactive),
        }),
      )
      .mockResolvedValue(active);
    authorityReads();
    authorityPreview();
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    });
    const user = userEvent.setup();
    let refresh: () => void = () => {
      throw new Error("Refresh driver not mounted");
    };
    render(
      <>
        <Dashboard />
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
      </>,
    );
    await beginUnknownAuthority(user);
    const [{ bytes }] = execute.mock.calls[0]!;
    await act(() => Promise.resolve().then(refresh));
    await screen.findByRole("heading", {
      name: kind === "DISABLED" ? "This account is disabled" : "You don’t have access yet",
    });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByRole("dialog")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Keep editing" }));
    await user.click(screen.getByRole("button", { name: "Check access" }));
    await user.click(await screen.findByRole("button", { name: "Continue current request" }));
    const retry = await screen.findByRole("button", { name: "Send again" });
    await user.click(retry);
    expect(execute).toHaveBeenCalledTimes(2);
    expect(execute.mock.calls[1]![0].bytes).toBe(bytes);
  },
);

it.each(["actor", "installation"] as const)(
  "does not retain a previous workspace for a NO_ROLES requester with a different %s",
  async (difference) => {
    const active = contextFor(
      ["CASES", "AUTHORITY", "OPERATIONS"],
      authorityTasks,
      "https://issuer.example/",
    );
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.document.context.state !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const source = active.value.outcome.document.context;
    const requester: HumanReadDocument_context = {
      screen: { regions: [] },
      context: {
        kind: "REQUESTER",
        state: "NO_ROLES",
        actorId: difference === "actor" ? "10000000-0000-4000-8000-000000000099" : source.actorId,
        installationId:
          difference === "installation"
            ? "90000000-0000-4000-8000-000000000099"
            : source.installationId,
        tasks: [],
        views: [],
        authorityIssuerSeed: null,
      },
      basis: { kind: "UNAVAILABLE" },
      disclosure: "CURRENT",
    };
    mockEndpoint("context")
      .mockResolvedValueOnce(active)
      .mockResolvedValueOnce(
        outcome({
          ...humanReadReply("context", requester),
        }),
      )
      .mockResolvedValue(active);
    authorityReads();
    authorityPreview();
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    });
    const user = userEvent.setup();
    let refresh: () => void = () => {
      throw new Error("Refresh driver not mounted");
    };
    render(
      <>
        <Dashboard />
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
      </>,
    );
    await beginUnknownAuthority(user);
    await act(() => Promise.resolve().then(refresh));
    await screen.findByRole("heading", { name: "You don’t have access yet" });
    expect(document.querySelector(".workspace-shell")).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Check access" }));
    await screen.findByRole("heading", { name: "People & access", level: 1 });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Continue current request" })).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
  },
);
