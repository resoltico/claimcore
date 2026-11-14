import { installationActions, activeRequester } from "../support/human-requester.fixtures";
import {
  caseSubject,
  caseDocumentInput,
  caseIdentityScreen,
} from "../support/case-document.fixtures";
import { operationAffordance } from "../support/affordances.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";

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
import { peoplePreview } from "../support/people-operation.fixtures";
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
type Views = ReadonlyArray<"CASES" | "PEOPLE" | "OPERATIONS">;
const peopleTasks: ReadonlyArray<TaskId> = [
  "case.open",
  "people.admit",
  "people.list",
  "people.grants",
  "people.history",
  "people.requests",
  "people.requests.dismiss",
];
const requesterFor = (
  views: Views,
  tasks: ReadonlyArray<TaskId>,
  issuer: string | null,
): HumanReadDocument_context => ({
  ...activeRequester,
  actions: installationActions(tasks, issuer),
  links: activeRequester.links.filter((link) =>
    views.some((view) => link.labelId === `view.${view}.label`),
  ),
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
const beginUnknownPeople = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByRole("link", { name: "People and access" }));
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
const peopleReads = () => {
  mockEndpoint("case.list").mockResolvedValue(
    outcome({
      ...humanReadReply("case.list", {
        screen: { regions: [] },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    }),
  );
  mockEndpoint("people.list")
    .mockResolvedValueOnce(
      outcome({
        ...humanReadReply("people.list", {
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
                      link: {
                        endpoint: "people.grants",
                        input: {
                          actorId: {
                            actorId: "00000000-0000-4000-8000-000000000001",
                            displayName: { kind: "NAMED", value: "PREVIOUS_ACTOR_CANARY" },
                            principalKind: "HUMAN",
                            availability: "AVAILABLE",
                          }.actorId,
                          limit: 20,
                        },
                      },
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
          subject: { kind: "LIST" },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "SNAPSHOT", observation: workBasis },
          disclosure: "CURRENT",
        }),
      }),
    )
    .mockResolvedValue(outcome({ representation: "HUMAN", outcome: unavailable }));
};
it("retains the exact uncertain people request when delayed context removes its navigation view", async () => {
  const delayed = deferred<ApiResult<ServiceResponse<"context">>>();
  const metadata = mockEndpoint("context")
    .mockResolvedValueOnce(
      contextFor(["CASES", "PEOPLE", "OPERATIONS"], peopleTasks, "https://issuer.example/"),
    )
    .mockReturnValue(delayed.promise);
  peopleReads();
  peoplePreview();
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
  await beginUnknownPeople(user);
  const original = execute.mock.calls[0]![0].bytes;
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(metadata.mock.calls.length).toBeGreaterThan(1);
  });
  await act(async () => {
    delayed.finish(contextFor(["CASES", "OPERATIONS"], ["case.open"]));
    await delayed.promise;
  });
  expect(screen.getByRole("heading", { name: "People & access", level: 1 })).toBeVisible();
  expect(screen.queryByRole("link", { name: "People and access" })).toBeNull();
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
      contextFor(["CASES", "PEOPLE", "OPERATIONS"], peopleTasks, "https://issuer.example/"),
    )
    .mockResolvedValueOnce(contextFor(["OPERATIONS"], []))
    .mockResolvedValue(
      contextFor(["CASES", "PEOPLE", "OPERATIONS"], peopleTasks, "https://issuer.example/"),
    );
  const visible = caseDocumentInput(
    {
      subject: { ...caseSubject, caseId: current.subject.caseId },
      actions: [
        operationAffordance(
          "case.close",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: current.subject.caseId },
            expectedRevision: 1,
          },
          {},
          undefined,
          {},
        ),
      ],
      history: "FULL",
    },
    {
      regions: [
        ...caseIdentityScreen.regions,
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
      ["CASES", "PEOPLE", "OPERATIONS"],
      peopleTasks,
      "https://issuer.example/",
    );
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.document.subject.state !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const self = active.value.outcome.document.subject;
    const inactive: HumanReadDocument_context = {
      screen: { regions: [] },
      subject: {
        kind: "REQUESTER",
        state: kind,
        installationId: kind === "NO_ROLES" ? self.installationId : null,
        actorId: self.actorId,
      },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
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
    peopleReads();
    peoplePreview();
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
    await beginUnknownPeople(user);
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
      ["CASES", "PEOPLE", "OPERATIONS"],
      peopleTasks,
      "https://issuer.example/",
    );
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.document.subject.state !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const source = active.value.outcome.document.subject;
    const requester: HumanReadDocument_context = {
      screen: { regions: [] },
      subject: {
        kind: "REQUESTER",
        state: "NO_ROLES",
        installationId:
          difference === "installation"
            ? "90000000-0000-4000-8000-000000000099"
            : source.installationId,
        actorId: difference === "actor" ? "10000000-0000-4000-8000-000000000099" : source.actorId,
      },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
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
    peopleReads();
    peoplePreview();
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
    await beginUnknownPeople(user);
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
